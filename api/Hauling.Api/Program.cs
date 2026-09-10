using System.Text.Json;
using System.Text.Json.Serialization;
using Hauling.Api.Services;
using Microsoft.Extensions.Logging;

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
var callbackUrl = Environment.GetEnvironmentVariable("EVE_SSO_CALLBACK") ?? "https://hauling.angry.no/callback";

var userRepo = new UserRepository(connStr);
var ssoService = new EveSsoService(clientId, clientSecret, callbackUrl);
var authService = new AuthService(jwtKey);
var itemRepo = new ItemRepository(connStr);
var orderRepo = new OrderRepository(connStr);
var routeRepo = new RouteRepository(connStr);

builder.Services.AddSingleton(userRepo);
builder.Services.AddSingleton(ssoService);
builder.Services.AddSingleton(authService);
builder.Services.AddSingleton(itemRepo);
builder.Services.AddSingleton(orderRepo);
builder.Services.AddSingleton(routeRepo);
// EsiMarketService needs an ILogger, so let DI build it (via a factory to stay AOT-friendly).
builder.Services.AddSingleton(sp => new EsiMarketService(sp.GetRequiredService<ILogger<EsiMarketService>>()));
var esiResolver = new EsiItemResolver();
builder.Services.AddSingleton(esiResolver);
var discordWebhook = Environment.GetEnvironmentVariable("DISCORD_WEBHOOK_HAULING") ?? "";
var discord = new DiscordNotifier(discordWebhook);
builder.Services.AddSingleton(discord);

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

// SSO callback — exchanges code for token, verifies corp membership, returns JWT
app.MapGet("/callback", async (string? code, string? state, EveSsoService sso, UserRepository repo, AuthService auth, CancellationToken ct) =>
{
    if (string.IsNullOrEmpty(code))
        return Results.BadRequest(new ErrorResponse { Error = "No authorization code received" });

    var charInfo = await sso.ExchangeCodeAsync(code, ct);
    if (charInfo == null)
        return Results.BadRequest(new ErrorResponse { Error = "Failed to authenticate with EVE SSO" });

    // Check corp membership
    var requiredCorp = await repo.GetConfigAsync("corp_id", ct);
    if (string.IsNullOrEmpty(requiredCorp) || charInfo.CorporationId.ToString() != requiredCorp)
    {
        return Results.Redirect($"/?denied={Uri.EscapeDataString(charInfo.CharacterName)}");
    }

    // Upsert user
    var user = await repo.UpsertUserAsync(charInfo.CharacterId, charInfo.CharacterName, charInfo.CorporationId, charInfo.AllianceId, ct);

    // Create our JWT
    var token = auth.CreateToken(user.CharacterId, user.CharacterName, user.Role);

    // Redirect to frontend with token
    return Results.Redirect($"/?token={token}");
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

// POST /api/items/match — bulk match item names against eve_types, ESI fallback for unknown items
app.MapPost("/api/items/match", async (List<string> names, ItemRepository items, EsiItemResolver resolver, CancellationToken ct) =>
{
    var results = await items.MatchNamesAsync(names, ct);

    // Find unmatched names
    var matchedNames = new HashSet<string>(results.Select(r => r.TypeName.ToLowerInvariant()));
    var unmatched = names.Where(n => !matchedNames.Contains(n.ToLowerInvariant())).ToList();

    if (unmatched.Count > 0)
    {
        // Resolve via ESI
        var resolved = await resolver.ResolveNamesAsync(unmatched, ct);
        foreach (var (typeId, name) in resolved)
        {
            var info = await resolver.GetTypeInfoAsync(typeId, ct);
            if (info != null)
            {
                // Insert into database for future lookups
                await items.InsertAsync(info.TypeId, info.TypeName, info.Volume, info.PackagedVolume, ct);
                var vol = info.PackagedVolume ?? info.Volume;
                results.Add(new ItemResult { TypeId = info.TypeId, TypeName = info.TypeName, Volume = vol });
            }
        }
    }

    return Results.Ok(results);
});

// GET /api/items/{typeId}/price — get Jita lowest sell price
app.MapGet("/api/items/{typeId:int}/price", async (int typeId, EsiMarketService esi, CancellationToken ct) =>
{
    // A failed lookup returns null; report 0 to the client (unchanged contract) but it's now
    // logged and retried server-side, and order creation backfills any 0 authoritatively.
    var price = await esi.TryGetJitaSellPriceAsync(typeId, ct);
    return Results.Ok(new PriceResponse { TypeId = typeId, JitaSellPrice = price ?? 0m });
});

// --- Orders (all require auth) ---

// Helper to extract claims from Bearer token
TokenClaims? GetClaims(HttpRequest request, AuthService auth)
{
    var token = request.Headers["Authorization"].FirstOrDefault()?.Replace("Bearer ", "");
    return string.IsNullOrEmpty(token) ? null : auth.ValidateToken(token);
}

async Task<(decimal haulingFee, decimal shopperFee)> CalculateFeesAsync(
    RouteInfo route, decimal totalM3, bool rush, bool shopRequested,
    UserRepository users, EsiMarketService esi, CancellationToken ct)
{
    var isotopeTypeId = int.Parse(await users.GetConfigAsync("isotope_type_id", ct));
    var isotopePrice = await esi.GetIsotopePriceAsync(isotopeTypeId, ct);
    var cargoCapacity = decimal.Parse(await users.GetConfigAsync("cargo_capacity_m3", ct));
    var fuelIskPerM3 = (isotopePrice * route.RoundTripIsotopes) / cargoCapacity;

    decimal pushxFee = 0;
    if (route.HasPushx)
    {
        var tierBreak = decimal.Parse(await users.GetConfigAsync("pushx_volume_tier_break", ct));
        var small = totalM3 <= tierBreak;
        var key = rush
            ? (small ? "pushx_rush_fee_small" : "pushx_rush_fee_large")
            : (small ? "pushx_fee_small" : "pushx_fee_large");
        pushxFee = decimal.Parse(await users.GetConfigAsync(key, ct));
    }

    var serviceIsk = decimal.Parse(await users.GetConfigAsync("service_isk_per_m3", ct));
    var haulingFee = pushxFee + (fuelIskPerM3 + serviceIsk) * totalM3;

    var shopperFeeConfig = decimal.Parse(await users.GetConfigAsync("shopper_fee", ct));
    var shopperFee = shopRequested && route.AllowsShopping ? shopperFeeConfig : 0;

    return (haulingFee, shopperFee);
}

// POST /api/orders — create a new order
app.MapPost("/api/orders", async (HttpRequest request, CreateOrderRequest body, AuthService auth,
    OrderRepository orders, UserRepository users, EsiMarketService esi, RouteRepository routes, CancellationToken ct) =>
{
    var claims = GetClaims(request, auth);
    if (claims == null) return Results.Unauthorized();

    var route = await routes.GetRouteAsync(body.OriginSystem, body.DestinationSystem, ct);
    if (route == null)
        return Results.BadRequest(new ErrorResponse { Error = $"Unsupported route {body.OriginSystem} → {body.DestinationSystem}" });

    var rush = body.Expedite && route.HasPushx;
    var shopRequested = body.ShopRequested && route.AllowsShopping;
    var maxM3 = decimal.Parse(await users.GetConfigAsync("max_order_m3", ct));

    var items = body.Items.Select(i => new OrderItemInput
    {
        TypeId = i.TypeId,
        Quantity = i.Quantity,
        VolumePerUnit = i.VolumePerUnit,
        EstimatedPrice = i.EstimatedPrice
    }).ToList();

    var totalM3 = items.Sum(i => i.VolumePerUnit * i.Quantity);
    if (totalM3 > maxM3)
        return Results.BadRequest(new ErrorResponse { Error = $"Order exceeds maximum capacity of {maxM3:N0} m³ ({totalM3:N2} m³ requested)" });

    var (haulingFee, shopperFee) = await CalculateFeesAsync(route, totalM3, rush, shopRequested, users, esi, ct);

    var orderId = await orders.CreateOrderAsync(claims.CharacterId, body.OriginSystem, body.DestinationSystem,
        shopRequested, rush, body.Notes, items, haulingFee, shopperFee, ct);

    _ = discord.NotifyNewOrderAsync(orderId, claims.CharacterName, body.OriginSystem, body.DestinationSystem,
        shopRequested, rush, totalM3, haulingFee, shopperFee, items.Count);

    return Results.Created($"/api/orders/{orderId}", new OrderCreatedResponse { OrderId = orderId });
});

// GET /api/orders — list orders (members see own, hauler/admin see all)
app.MapGet("/api/orders", async (HttpRequest request, AuthService auth, OrderRepository orders, int? limit, int? offset, CancellationToken ct) =>
{
    var claims = GetClaims(request, auth);
    if (claims == null) return Results.Unauthorized();

    long? filterCharId = claims.Role == "member" ? claims.CharacterId : null;
    var result = await orders.ListOrdersAsync(filterCharId, limit ?? 20, offset ?? 0, archivedOnly: false, ct);
    return Results.Ok(result);
});

// GET /api/orders/archived — list archived orders (admin only)
app.MapGet("/api/orders/archived", async (HttpRequest request, AuthService auth,
    OrderRepository orders, int? limit, int? offset, CancellationToken ct) =>
{
    var claims = GetClaims(request, auth);
    if (claims == null) return Results.Unauthorized();
    if (claims.Role != "admin")
        return Results.Json(new ErrorResponse { Error = "Admin only" }, HaulingJsonContext.Default.ErrorResponse, statusCode: 403);

    var result = await orders.ListOrdersAsync(null, limit ?? 50, offset ?? 0, archivedOnly: true, ct);
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

    var validStatuses = new[] { "pending", "accepted", "picking_up", "in_transit", "delivered", "cancelled" };
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
    AuthService auth, OrderRepository orders, UserRepository users, EsiMarketService esi, RouteRepository routes, CancellationToken ct) =>
{
    var claims = GetClaims(request, auth);
    if (claims == null) return Results.Unauthorized();

    var order = await orders.GetOrderAsync(id, ct);
    if (order == null) return Results.NotFound();
    if (order.Archived)
        return Results.Json(new ErrorResponse { Error = "Cannot modify archived order" }, HaulingJsonContext.Default.ErrorResponse, statusCode: 400);

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

    var originSystem = !string.IsNullOrEmpty(body.OriginSystem) ? body.OriginSystem : order.OriginSystem;
    var destinationSystem = !string.IsNullOrEmpty(body.DestinationSystem) ? body.DestinationSystem : order.DestinationSystem;

    var route = await routes.GetRouteAsync(originSystem, destinationSystem, ct);
    if (route == null)
        return Results.BadRequest(new ErrorResponse { Error = $"Unsupported route {originSystem} → {destinationSystem}" });

    var rush = body.Expedite && route.HasPushx;
    var shopRequested = body.ShopRequested && route.AllowsShopping;

    var itemInputs = body.Items.Select(i => new OrderItemInput
    {
        TypeId = i.TypeId,
        Quantity = i.Quantity,
        VolumePerUnit = i.VolumePerUnit,
        EstimatedPrice = i.EstimatedPrice
    }).ToList();

    var editTotalM3 = itemInputs.Sum(i => i.VolumePerUnit * i.Quantity);
    var (editHaulingFee, editShopperFee) = await CalculateFeesAsync(route, editTotalM3, rush, shopRequested, users, esi, ct);

    await orders.ReplaceOrderItemsAsync(id, originSystem, destinationSystem, shopRequested, rush, body.Notes, itemInputs, editHaulingFee, editShopperFee, ct);
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

// PUT /api/orders/{id}/archive — archive a delivered/cancelled order (admin only, no unarchive)
app.MapPut("/api/orders/{id:long}/archive", async (long id, HttpRequest request,
    AuthService auth, OrderRepository orders, CancellationToken ct) =>
{
    var claims = GetClaims(request, auth);
    if (claims == null) return Results.Unauthorized();
    if (claims.Role != "admin")
        return Results.Json(new ErrorResponse { Error = "Admin only" }, HaulingJsonContext.Default.ErrorResponse, statusCode: 403);

    var order = await orders.GetOrderAsync(id, ct);
    if (order == null) return Results.NotFound();
    if (order.Archived)
        return Results.Json(new ErrorResponse { Error = "Order is already archived" }, HaulingJsonContext.Default.ErrorResponse, statusCode: 400);
    if (order.Status != "delivered" && order.Status != "cancelled")
        return Results.Json(new ErrorResponse { Error = "Only delivered or cancelled orders can be archived" }, HaulingJsonContext.Default.ErrorResponse, statusCode: 400);

    var ok = await orders.ArchiveOrderAsync(id, ct);
    return ok ? Results.Ok(new HealthResponse { Status = "archived" }) : Results.NotFound();
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

app.MapGet("/api/config", async (UserRepository users, RouteRepository routeRepo, EsiMarketService esi, CancellationToken ct) =>
{
    var allRoutes = await routeRepo.GetAllRoutesAsync(ct);
    var isotopeTypeId = int.Parse(await users.GetConfigAsync("isotope_type_id", ct));
    var isotopePrice = await esi.GetIsotopePriceAsync(isotopeTypeId, ct);
    var cargoCapacity = decimal.Parse(await users.GetConfigAsync("cargo_capacity_m3", ct));
    var pushxFeeSmall = decimal.Parse(await users.GetConfigAsync("pushx_fee_small", ct));
    var pushxFeeLarge = decimal.Parse(await users.GetConfigAsync("pushx_fee_large", ct));
    var pushxRushFeeSmall = decimal.Parse(await users.GetConfigAsync("pushx_rush_fee_small", ct));
    var pushxRushFeeLarge = decimal.Parse(await users.GetConfigAsync("pushx_rush_fee_large", ct));
    var pushxVolumeTierBreak = decimal.Parse(await users.GetConfigAsync("pushx_volume_tier_break", ct));

    var routeConfigs = allRoutes.Select(r => new RouteConfigResponse
    {
        Origin = r.Origin,
        Destination = r.Destination,
        RoundTripIsotopes = r.RoundTripIsotopes,
        FuelIskPerM3 = (isotopePrice * r.RoundTripIsotopes) / cargoCapacity,
        HasPushx = r.HasPushx,
        AllowsShopping = r.AllowsShopping,
        LabelOrigin = r.LabelOrigin,
        LabelDestination = r.LabelDestination,
        PushxFeeSmall = pushxFeeSmall,
        PushxFeeLarge = pushxFeeLarge,
        PushxRushFeeSmall = pushxRushFeeSmall,
        PushxRushFeeLarge = pushxRushFeeLarge,
        PushxVolumeTierBreak = pushxVolumeTierBreak
    }).ToList();

    return Results.Ok(new ConfigResponse
    {
        Routes = routeConfigs,
        ServiceIskPerM3 = decimal.Parse(await users.GetConfigAsync("service_isk_per_m3", ct)),
        ShopperFee = decimal.Parse(await users.GetConfigAsync("shopper_fee", ct)),
        MaxOrderM3 = decimal.Parse(await users.GetConfigAsync("max_order_m3", ct)),
        IsotopePrice = isotopePrice,
        CargoCapacity = cargoCapacity
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
    public bool Expedite { get; set; }
    public string Notes { get; set; } = "";
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
    public List<RouteConfigResponse> Routes { get; set; } = new();
    public decimal ServiceIskPerM3 { get; set; }
    public decimal ShopperFee { get; set; }
    public decimal MaxOrderM3 { get; set; }
    public decimal IsotopePrice { get; set; }
    public decimal CargoCapacity { get; set; }
}

public sealed class RouteConfigResponse
{
    public string Origin { get; set; } = "";
    public string Destination { get; set; } = "";
    public int RoundTripIsotopes { get; set; }
    public decimal FuelIskPerM3 { get; set; }
    public bool HasPushx { get; set; }
    public bool AllowsShopping { get; set; }
    public string? LabelOrigin { get; set; }
    public string? LabelDestination { get; set; }
    public decimal PushxFeeSmall { get; set; }
    public decimal PushxFeeLarge { get; set; }
    public decimal PushxRushFeeSmall { get; set; }
    public decimal PushxRushFeeLarge { get; set; }
    public decimal PushxVolumeTierBreak { get; set; }
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
[JsonSerializable(typeof(RouteConfigResponse))]
[JsonSerializable(typeof(List<RouteConfigResponse>))]
[JsonSerializable(typeof(List<OrderSummary>))]
[JsonSerializable(typeof(OrderDetail))]
[JsonSerializable(typeof(OrderItemDetail))]
[JsonSerializable(typeof(List<OrderItemDetail>))]
[JsonSerializable(typeof(List<HaulerInfo>))]
[JsonSerializable(typeof(HaulerInfo))]
internal partial class HaulingJsonContext : JsonSerializerContext { }
