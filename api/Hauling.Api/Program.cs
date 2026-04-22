using System.Text.Json;
using System.Text.Json.Serialization;
using Hauling.Api.Services;

var builder = WebApplication.CreateSlimBuilder(args);

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, HaulingJsonContext.Default);
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
});

var connStr = Environment.GetEnvironmentVariable("HAULING_DB") ?? "";
var jwtKey = Environment.GetEnvironmentVariable("HAULING_JWT_KEY") ?? "default-dev-key-change-me-in-prod";
var clientId = Environment.GetEnvironmentVariable("EVE_SSO_CLIENT_ID") ?? "";
var clientSecret = Environment.GetEnvironmentVariable("EVE_SSO_CLIENT_SECRET") ?? "";
var callbackUrl = Environment.GetEnvironmentVariable("EVE_SSO_CALLBACK") ?? "https://bendigo7.com/hauling/callback";

var userRepo = new UserRepository(connStr);
var ssoService = new EveSsoService(clientId, clientSecret, callbackUrl);
var authService = new AuthService(jwtKey);
var itemRepo = new ItemRepository(connStr);
var orderRepo = new OrderRepository(connStr);
var esiMarket = new EsiMarketService();

builder.Services.AddSingleton(userRepo);
builder.Services.AddSingleton(ssoService);
builder.Services.AddSingleton(authService);
builder.Services.AddSingleton(itemRepo);
builder.Services.AddSingleton(orderRepo);
builder.Services.AddSingleton(esiMarket);

var app = builder.Build();

// Health check
app.MapGet("/api/health", () => Results.Ok(new HealthResponse { Status = "ok" }));

// Start SSO login — returns the EVE SSO URL to redirect to
app.MapGet("/api/auth/login", (EveSsoService sso) =>
{
    var state = Guid.NewGuid().ToString("N");
    var url = sso.GetAuthorizeUrl(state);
    return Results.Ok(new LoginResponse { Url = url, State = state });
});

// SSO callback — exchanges code for token, verifies alliance, returns JWT
app.MapGet("/callback", async (string? code, string? state, EveSsoService sso, UserRepository repo, AuthService auth, CancellationToken ct) =>
{
    if (string.IsNullOrEmpty(code))
        return Results.BadRequest(new ErrorResponse { Error = "No authorization code received" });

    var charInfo = await sso.ExchangeCodeAsync(code, ct);
    if (charInfo == null)
        return Results.BadRequest(new ErrorResponse { Error = "Failed to authenticate with EVE SSO" });

    // Check alliance membership
    var requiredAlliance = await repo.GetConfigAsync("alliance_id", ct);
    if (string.IsNullOrEmpty(requiredAlliance) || charInfo.AllianceId?.ToString() != requiredAlliance)
    {
        return Results.Json(new ErrorResponse { Error = $"Access restricted to alliance members. Character {charInfo.CharacterName} is not in the required alliance." },
            HaulingJsonContext.Default.ErrorResponse, statusCode: 403);
    }

    // Upsert user
    var user = await repo.UpsertUserAsync(charInfo.CharacterId, charInfo.CharacterName, charInfo.CorporationId, charInfo.AllianceId, ct);

    // Create our JWT
    var token = auth.CreateToken(user.CharacterId, user.CharacterName, user.Role);

    // Redirect to frontend with token
    return Results.Redirect($"/hauling/?token={token}");
});

// Get current user info from JWT
app.MapGet("/api/auth/me", (HttpRequest request, AuthService auth) =>
{
    var token = request.Headers["Authorization"].FirstOrDefault()?.Replace("Bearer ", "");
    if (string.IsNullOrEmpty(token)) return Results.Unauthorized();

    var claims = auth.ValidateToken(token);
    if (claims == null) return Results.Unauthorized();

    return Results.Ok(new MeResponse
    {
        CharacterId = claims.CharacterId,
        CharacterName = claims.CharacterName,
        Role = claims.Role
    });
});

// --- Item Search ---

// GET /api/items/search?q=tritanium&limit=20
app.MapGet("/api/items/search", async (string q, int? limit, ItemRepository items, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(q) || q.Length < 2)
        return Results.BadRequest(new ErrorResponse { Error = "Search query must be at least 2 characters" });
    var results = await items.SearchAsync(q, limit ?? 20, ct);
    return Results.Ok(results);
});

// GET /api/items/{typeId}/price — get Jita lowest sell price
app.MapGet("/api/items/{typeId:int}/price", async (int typeId, EsiMarketService esi, CancellationToken ct) =>
{
    var price = await esi.GetJitaSellPriceAsync(typeId, ct);
    return Results.Ok(new PriceResponse { TypeId = typeId, JitaSellPrice = price });
});

// --- Orders (all require auth) ---

// Helper to extract claims from Bearer token
TokenClaims? GetClaims(HttpRequest request, AuthService auth)
{
    var token = request.Headers["Authorization"].FirstOrDefault()?.Replace("Bearer ", "");
    return string.IsNullOrEmpty(token) ? null : auth.ValidateToken(token);
}

// POST /api/orders — create a new order
app.MapPost("/api/orders", async (HttpRequest request, CreateOrderRequest body, AuthService auth,
    OrderRepository orders, UserRepository users, CancellationToken ct) =>
{
    var claims = GetClaims(request, auth);
    if (claims == null) return Results.Unauthorized();

    var haulingRate = decimal.Parse(await users.GetConfigAsync("hauling_rate_per_m3", ct));
    var shopperPct = decimal.Parse(await users.GetConfigAsync("shopper_fee_pct", ct));

    var items = body.Items.Select(i => new OrderItemInput
    {
        TypeId = i.TypeId,
        Quantity = i.Quantity,
        VolumePerUnit = i.VolumePerUnit,
        EstimatedPrice = i.EstimatedPrice
    }).ToList();

    var orderId = await orders.CreateOrderAsync(claims.CharacterId, body.ShopRequested, items, haulingRate, shopperPct, ct);
    return Results.Created($"/api/orders/{orderId}", new OrderCreatedResponse { OrderId = orderId });
});

// GET /api/orders — list orders (members see own, jf_pilot/admin see all)
app.MapGet("/api/orders", async (HttpRequest request, AuthService auth, OrderRepository orders, int? limit, int? offset, CancellationToken ct) =>
{
    var claims = GetClaims(request, auth);
    if (claims == null) return Results.Unauthorized();

    long? filterCharId = claims.Role == "member" ? claims.CharacterId : null;
    var result = await orders.ListOrdersAsync(filterCharId, limit ?? 20, offset ?? 0, ct);
    return Results.Ok(result);
});

// GET /api/orders/{id} — get order detail
app.MapGet("/api/orders/{id:long}", async (long id, HttpRequest request, AuthService auth, OrderRepository orders, CancellationToken ct) =>
{
    var claims = GetClaims(request, auth);
    if (claims == null) return Results.Unauthorized();

    var order = await orders.GetOrderAsync(id, ct);
    if (order == null) return Results.NotFound();

    // Members can only see their own orders
    if (claims.Role == "member" && order.CharacterId != claims.CharacterId)
        return Results.Json(new ErrorResponse { Error = "Access denied" }, HaulingJsonContext.Default.ErrorResponse, statusCode: 403);

    return Results.Ok(order);
});

// PUT /api/orders/{id}/status — update order status (jf_pilot/admin only)
app.MapPut("/api/orders/{id:long}/status", async (long id, HttpRequest request, UpdateStatusRequest body,
    AuthService auth, OrderRepository orders, CancellationToken ct) =>
{
    var claims = GetClaims(request, auth);
    if (claims == null) return Results.Unauthorized();
    if (claims.Role == "member")
        return Results.Json(new ErrorResponse { Error = "Only JF pilots and admins can update order status" },
            HaulingJsonContext.Default.ErrorResponse, statusCode: 403);

    var validStatuses = new[] { "pending", "accepted", "in_transit", "delivered", "cancelled" };
    if (!validStatuses.Contains(body.Status))
        return Results.BadRequest(new ErrorResponse { Error = $"Invalid status. Must be one of: {string.Join(", ", validStatuses)}" });

    long? assignedTo = body.Status == "accepted" ? claims.CharacterId : null;
    var updated = await orders.UpdateStatusAsync(id, body.Status, assignedTo, ct);
    return updated ? Results.Ok(new StatusResponse { OrderId = id, Status = body.Status }) : Results.NotFound();
});

// PUT /api/orders/items/{itemId}/actual-price — set actual price (jf_pilot/admin only)
app.MapPut("/api/orders/items/{itemId:long}/actual-price", async (long itemId, HttpRequest request,
    UpdatePriceRequest body, AuthService auth, OrderRepository orders, CancellationToken ct) =>
{
    var claims = GetClaims(request, auth);
    if (claims == null) return Results.Unauthorized();
    if (claims.Role == "member")
        return Results.Json(new ErrorResponse { Error = "Only JF pilots and admins can set actual prices" },
            HaulingJsonContext.Default.ErrorResponse, statusCode: 403);

    var updated = await orders.UpdateActualPriceAsync(itemId, body.ActualPrice, ct);
    return updated ? Results.Ok(new HealthResponse { Status = "updated" }) : Results.NotFound();
});

// GET /api/config — get fee rates (public for display)
app.MapGet("/api/config", async (UserRepository users, CancellationToken ct) =>
{
    var haulingRate = await users.GetConfigAsync("hauling_rate_per_m3", ct);
    var shopperPct = await users.GetConfigAsync("shopper_fee_pct", ct);
    return Results.Ok(new ConfigResponse { HaulingRatePerM3 = decimal.Parse(haulingRate), ShopperFeePct = decimal.Parse(shopperPct) });
});

app.Run();

// Response types
public sealed class HealthResponse { public string Status { get; set; } = ""; }
public sealed class ErrorResponse { public string Error { get; set; } = ""; }
public sealed class LoginResponse { public string Url { get; set; } = ""; public string State { get; set; } = ""; }
public sealed class MeResponse { public long CharacterId { get; set; } public string CharacterName { get; set; } = ""; public string Role { get; set; } = ""; }

// Request types
public sealed class CreateOrderRequest
{
    public bool ShopRequested { get; set; }
    public List<CreateOrderItemRequest> Items { get; set; } = new();
}
public sealed class CreateOrderItemRequest
{
    public int TypeId { get; set; }
    public int Quantity { get; set; }
    public decimal VolumePerUnit { get; set; }
    public decimal EstimatedPrice { get; set; }
}
public sealed class UpdateStatusRequest { public string Status { get; set; } = ""; }
public sealed class UpdatePriceRequest { public decimal ActualPrice { get; set; } }

// Additional response types
public sealed class PriceResponse { public int TypeId { get; set; } public decimal JitaSellPrice { get; set; } }
public sealed class OrderCreatedResponse { public long OrderId { get; set; } }
public sealed class StatusResponse { public long OrderId { get; set; } public string Status { get; set; } = ""; }
public sealed class ConfigResponse { public decimal HaulingRatePerM3 { get; set; } public decimal ShopperFeePct { get; set; } }

// AOT JSON source generator
[JsonSerializable(typeof(HealthResponse))]
[JsonSerializable(typeof(ErrorResponse))]
[JsonSerializable(typeof(LoginResponse))]
[JsonSerializable(typeof(MeResponse))]
[JsonSerializable(typeof(List<ItemResult>))]
[JsonSerializable(typeof(PriceResponse))]
[JsonSerializable(typeof(CreateOrderRequest))]
[JsonSerializable(typeof(CreateOrderItemRequest))]
[JsonSerializable(typeof(List<CreateOrderItemRequest>))]
[JsonSerializable(typeof(UpdateStatusRequest))]
[JsonSerializable(typeof(UpdatePriceRequest))]
[JsonSerializable(typeof(OrderCreatedResponse))]
[JsonSerializable(typeof(StatusResponse))]
[JsonSerializable(typeof(ConfigResponse))]
[JsonSerializable(typeof(List<OrderSummary>))]
[JsonSerializable(typeof(OrderDetail))]
[JsonSerializable(typeof(OrderItemDetail))]
[JsonSerializable(typeof(List<OrderItemDetail>))]
internal partial class HaulingJsonContext : JsonSerializerContext { }
