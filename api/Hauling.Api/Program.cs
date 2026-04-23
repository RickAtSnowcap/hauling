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
        return Results.Redirect($"/hauling/?denied={Uri.EscapeDataString(charInfo.CharacterName)}");
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

// GET /api/haulers — list haulers and admins (for assign dropdown)
app.MapGet("/api/haulers", async (HttpRequest request, AuthService auth, UserRepository users, CancellationToken ct) =>
{
    var claims = GetClaims(request, auth);
    if (claims == null) return Results.Unauthorized();
    if (claims.Role != "admin")
        return Results.Json(new ErrorResponse { Error = "Admin only" }, HaulingJsonContext.Default.ErrorResponse, statusCode: 403);

    var haulers = await users.ListHaulersAsync(ct);
    return Results.Ok(haulers);
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

// POST /api/items/match — bulk match item names against eve_types
app.MapPost("/api/items/match", async (List<string> names, ItemRepository items, CancellationToken ct) =>
{
    var results = await items.MatchNamesAsync(names, ct);
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

    // Validate origin/destination
    var validOrigins = new[] { "Jita", "Odebeinn" };
    var validDestinations = new[] { "E-B957", "E-BYOS" };
    if (!validOrigins.Contains(body.OriginSystem))
        return Results.BadRequest(new ErrorResponse { Error = $"Invalid origin system. Must be one of: {string.Join(", ", validOrigins)}" });
    if (!validDestinations.Contains(body.DestinationSystem))
        return Results.BadRequest(new ErrorResponse { Error = $"Invalid destination system. Must be one of: {string.Join(", ", validDestinations)}" });

    // Get rate based on origin
    var rateKey = body.OriginSystem == "Jita" ? "jita_rate_per_m3" : "odebeinn_rate_per_m3";
    var haulingRate = decimal.Parse(await users.GetConfigAsync(rateKey, ct));
    var shopperFeePerItem = decimal.Parse(await users.GetConfigAsync("shopper_fee_per_item", ct));
    var shopperFeeMinimum = decimal.Parse(await users.GetConfigAsync("shopper_fee_minimum", ct));
    var maxM3 = decimal.Parse(await users.GetConfigAsync("max_order_m3", ct));

    var items = body.Items.Select(i => new OrderItemInput
    {
        TypeId = i.TypeId,
        Quantity = i.Quantity,
        VolumePerUnit = i.VolumePerUnit,
        EstimatedPrice = i.EstimatedPrice
    }).ToList();

    // Check total m3 against max
    var totalM3 = items.Sum(i => i.VolumePerUnit * i.Quantity);
    if (totalM3 > maxM3)
        return Results.BadRequest(new ErrorResponse { Error = $"Order exceeds maximum capacity of {maxM3:N0} m³ ({totalM3:N2} m³ requested)" });

    var orderId = await orders.CreateOrderAsync(claims.CharacterId, body.OriginSystem, body.DestinationSystem,
        body.ShopRequested, items, haulingRate, shopperFeePerItem, shopperFeeMinimum, ct);
    return Results.Created($"/api/orders/{orderId}", new OrderCreatedResponse { OrderId = orderId });
});

// GET /api/orders — list orders (members see own, hauler/admin see all)
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

// PUT /api/orders/{id}/status — update order status (hauler/admin only)
app.MapPut("/api/orders/{id:long}/status", async (long id, HttpRequest request, UpdateStatusRequest body,
    AuthService auth, OrderRepository orders, CancellationToken ct) =>
{
    var claims = GetClaims(request, auth);
    if (claims == null) return Results.Unauthorized();
    if (claims.Role == "member")
        return Results.Json(new ErrorResponse { Error = "Only haulers and admins can update order status" },
            HaulingJsonContext.Default.ErrorResponse, statusCode: 403);

    var validStatuses = new[] { "pending", "accepted", "in_transit", "delivered", "cancelled" };
    if (!validStatuses.Contains(body.Status))
        return Results.BadRequest(new ErrorResponse { Error = $"Invalid status. Must be one of: {string.Join(", ", validStatuses)}" });

    // Auto-assign hauler on accept, leave assigned_to unchanged for other transitions
    if (body.Status == "accepted")
    {
        await orders.AssignHaulerAsync(id, claims.CharacterId, ct);
    }
    var updated = await orders.UpdateStatusOnlyAsync(id, body.Status, ct);
    return updated ? Results.Ok(new StatusResponse { OrderId = id, Status = body.Status }) : Results.NotFound();
});

// PUT /api/orders/{id}/items — edit order line items (member own order while pending/accepted, or admin)
app.MapPut("/api/orders/{id:long}/items", async (long id, HttpRequest request, CreateOrderRequest body,
    AuthService auth, OrderRepository orders, UserRepository users, CancellationToken ct) =>
{
    var claims = GetClaims(request, auth);
    if (claims == null) return Results.Unauthorized();

    var order = await orders.GetOrderAsync(id, ct);
    if (order == null) return Results.NotFound();

    // Members can edit their own orders while pending/accepted
    // Admins can edit any order that's not delivered
    if (claims.Role == "member")
    {
        if (order.CharacterId != claims.CharacterId)
            return Results.Json(new ErrorResponse { Error = "Access denied" }, HaulingJsonContext.Default.ErrorResponse, statusCode: 403);
        if (order.Status != "pending" && order.Status != "accepted")
            return Results.Json(new ErrorResponse { Error = "Cannot edit order once in transit" }, HaulingJsonContext.Default.ErrorResponse, statusCode: 400);
    }
    else if (claims.Role == "hauler")
    {
        return Results.Json(new ErrorResponse { Error = "Haulers cannot edit order items" }, HaulingJsonContext.Default.ErrorResponse, statusCode: 403);
    }
    // admin can edit

    // Get rate based on order's origin system
    var rateKey = order.OriginSystem == "Jita" ? "jita_rate_per_m3" : "odebeinn_rate_per_m3";
    var haulingRate = decimal.Parse(await users.GetConfigAsync(rateKey, ct));
    var shopperFeePerItem = decimal.Parse(await users.GetConfigAsync("shopper_fee_per_item", ct));
    var shopperFeeMinimum = decimal.Parse(await users.GetConfigAsync("shopper_fee_minimum", ct));

    var itemInputs = body.Items.Select(i => new OrderItemInput
    {
        TypeId = i.TypeId,
        Quantity = i.Quantity,
        VolumePerUnit = i.VolumePerUnit,
        EstimatedPrice = i.EstimatedPrice
    }).ToList();

    await orders.ReplaceOrderItemsAsync(id, body.ShopRequested, itemInputs, haulingRate, shopperFeePerItem, shopperFeeMinimum, ct);
    return Results.Ok(new HealthResponse { Status = "updated" });
});

// PUT /api/orders/{id}/assign — assign hauler (admin only)
app.MapPut("/api/orders/{id:long}/assign", async (long id, HttpRequest request, AssignRequest body,
    AuthService auth, OrderRepository orders, CancellationToken ct) =>
{
    var claims = GetClaims(request, auth);
    if (claims == null) return Results.Unauthorized();
    if (claims.Role != "admin")
        return Results.Json(new ErrorResponse { Error = "Admin only" }, HaulingJsonContext.Default.ErrorResponse, statusCode: 403);

    var updated = await orders.AssignHaulerAsync(id, body.CharacterId, ct);
    return updated ? Results.Ok(new HealthResponse { Status = "assigned" }) : Results.NotFound();
});

// DELETE /api/orders/{id} — delete order (admin only)
app.MapDelete("/api/orders/{id:long}", async (long id, HttpRequest request, AuthService auth,
    OrderRepository orders, CancellationToken ct) =>
{
    var claims = GetClaims(request, auth);
    if (claims == null) return Results.Unauthorized();
    if (claims.Role != "admin")
        return Results.Json(new ErrorResponse { Error = "Admin only" }, HaulingJsonContext.Default.ErrorResponse, statusCode: 403);

    var deleted = await orders.DeleteOrderAsync(id, ct);
    return deleted ? Results.Ok(new HealthResponse { Status = "deleted" }) : Results.NotFound();
});

// PUT /api/orders/items/{itemId}/actual-price — set actual price (hauler/admin only)
app.MapPut("/api/orders/items/{itemId:long}/actual-price", async (long itemId, HttpRequest request,
    UpdatePriceRequest body, AuthService auth, OrderRepository orders, CancellationToken ct) =>
{
    var claims = GetClaims(request, auth);
    if (claims == null) return Results.Unauthorized();
    if (claims.Role == "member")
        return Results.Json(new ErrorResponse { Error = "Only haulers and admins can set actual prices" },
            HaulingJsonContext.Default.ErrorResponse, statusCode: 403);

    var orderId = await orders.UpdateActualPriceAsync(itemId, body.ActualPrice, ct);
    if (orderId == null) return Results.NotFound();
    await orders.RecalcTotalActualAsync(orderId.Value, ct);
    return Results.Ok(new HealthResponse { Status = "updated" });
});

// GET /api/config — get fee rates (public for display)
app.MapGet("/api/config", async (UserRepository users, CancellationToken ct) =>
{
    var jitaRate = await users.GetConfigAsync("jita_rate_per_m3", ct);
    var odebeinnRate = await users.GetConfigAsync("odebeinn_rate_per_m3", ct);
    var shopperFeePerItem = await users.GetConfigAsync("shopper_fee_per_item", ct);
    var shopperFeeMinimum = await users.GetConfigAsync("shopper_fee_minimum", ct);
    var maxM3 = await users.GetConfigAsync("max_order_m3", ct);
    return Results.Ok(new ConfigResponse
    {
        JitaRatePerM3 = decimal.Parse(jitaRate),
        OdebeinnRatePerM3 = decimal.Parse(odebeinnRate),
        ShopperFeePerItem = decimal.Parse(shopperFeePerItem),
        ShopperFeeMinimum = decimal.Parse(shopperFeeMinimum),
        MaxOrderM3 = decimal.Parse(maxM3)
    });
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
    public string OriginSystem { get; set; } = "";
    public string DestinationSystem { get; set; } = "";
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
public sealed class AssignRequest { public long CharacterId { get; set; } }

// Additional response types
public sealed class PriceResponse { public int TypeId { get; set; } public decimal JitaSellPrice { get; set; } }
public sealed class OrderCreatedResponse { public long OrderId { get; set; } }
public sealed class StatusResponse { public long OrderId { get; set; } public string Status { get; set; } = ""; }
public sealed class ConfigResponse
{
    public decimal JitaRatePerM3 { get; set; }
    public decimal OdebeinnRatePerM3 { get; set; }
    public decimal ShopperFeePerItem { get; set; }
    public decimal ShopperFeeMinimum { get; set; }
    public decimal MaxOrderM3 { get; set; }
}

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
[JsonSerializable(typeof(AssignRequest))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(OrderCreatedResponse))]
[JsonSerializable(typeof(StatusResponse))]
[JsonSerializable(typeof(ConfigResponse))]
[JsonSerializable(typeof(List<OrderSummary>))]
[JsonSerializable(typeof(OrderDetail))]
[JsonSerializable(typeof(OrderItemDetail))]
[JsonSerializable(typeof(List<OrderItemDetail>))]
[JsonSerializable(typeof(List<HaulerInfo>))]
[JsonSerializable(typeof(HaulerInfo))]
internal partial class HaulingJsonContext : JsonSerializerContext { }
