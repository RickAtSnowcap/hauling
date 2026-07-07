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
var callbackUrl = Environment.GetEnvironmentVariable("EVE_SSO_CALLBACK") ?? "https://bendigo7.com/hauling/callback";

var userRepo = new UserRepository(connStr);
var ssoService = new EveSsoService(clientId, clientSecret, callbackUrl);
var authService = new AuthService(jwtKey);
var itemRepo = new ItemRepository(connStr);
var orderRepo = new OrderRepository(connStr);

builder.Services.AddSingleton(userRepo);
builder.Services.AddSingleton(ssoService);
builder.Services.AddSingleton(authService);
builder.Services.AddSingleton(itemRepo);
builder.Services.AddSingleton(orderRepo);
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

// AMA Hauling supported routes: every trip has Z19-B8 as one endpoint, the other is Jita or Odebeinn.
// Returns null if valid, an error string otherwise.
static string? ValidateRoute(string origin, string destination)
{
    var inbound = (origin == "Jita" || origin == "Odebeinn") && destination == "Z19-B8";
    var outbound = origin == "Z19-B8" && (destination == "Jita" || destination == "Odebeinn");
    if (inbound || outbound) return null;
    return $"Unsupported route {origin} → {destination}. Valid routes: Jita↔Z19-B8, Odebeinn↔Z19-B8.";
}

// POST /api/orders — create a new order
app.MapPost("/api/orders", async (HttpRequest request, CreateOrderRequest body, AuthService auth,
    OrderRepository orders, UserRepository users, EsiMarketService esi, CancellationToken ct) =>
{
    var claims = GetClaims(request, auth);
    if (claims == null) return Results.Unauthorized();

    // Validate the route pair — every trip must have Z19-B8 as one endpoint
    var routeError = ValidateRoute(body.OriginSystem, body.DestinationSystem);
    if (routeError != null) return Results.BadRequest(new ErrorResponse { Error = routeError });

    // Evola handles the highsec leg whenever Jita is an endpoint (in either direction)
    var evolaRoute = body.OriginSystem == "Jita" || body.DestinationSystem == "Jita";
    var expedite = body.Expedite && evolaRoute;
    // Shopper only makes sense when buying at Jita — origin must be Jita
    var shopRequested = body.ShopRequested && body.OriginSystem == "Jita";

    // Read pricing components (Path B — Evola pass-through model)
    var evolaIsk = decimal.Parse(await users.GetConfigAsync("evola_isk_per_m3", ct));
    var evolaPct = decimal.Parse(await users.GetConfigAsync("evola_collateral_pct", ct));
    var evolaMin = decimal.Parse(await users.GetConfigAsync("evola_minimum", ct));
    var fuelIsk = decimal.Parse(await users.GetConfigAsync("fuel_isk_per_m3", ct));
    var serviceIsk = decimal.Parse(await users.GetConfigAsync("service_isk_per_m3", ct));
    var shopperFeePerItem = decimal.Parse(await users.GetConfigAsync("shopper_fee_per_item", ct));
    var shopperFeeMinimum = decimal.Parse(await users.GetConfigAsync("shopper_fee_minimum", ct));
    var expediteFee = expedite ? decimal.Parse(await users.GetConfigAsync("expedite_fee", ct)) : 0;
    var maxM3 = decimal.Parse(await users.GetConfigAsync("max_order_m3", ct));

    var items = body.Items.Select(i => new OrderItemInput
    {
        TypeId = i.TypeId,
        Quantity = i.Quantity,
        VolumePerUnit = i.VolumePerUnit,
        EstimatedPrice = i.EstimatedPrice
    }).ToList();

    // Server-side price backfill. The client fetches Jita prices opportunistically and can
    // silently leave a line at 0 (e.g. ESI throttled the last item of a large order). On an
    // Evola route the estimate feeds the 1% collateral charge, so a stray 0 undercharges the
    // haul — fill any zero-priced line here authoritatively before the math and the save.
    if (evolaRoute)
    {
        foreach (var item in items.Where(i => i.EstimatedPrice == 0 && i.TypeId > 0))
        {
            var price = await esi.TryGetJitaSellPriceAsync(item.TypeId, ct);
            if (price is > 0) item.EstimatedPrice = price.Value;
        }
    }

    // Check total m3 against max
    var totalM3 = items.Sum(i => i.VolumePerUnit * i.Quantity);
    if (totalM3 > maxM3)
        return Results.BadRequest(new ErrorResponse { Error = $"Order exceeds maximum capacity of {maxM3:N0} m³ ({totalM3:N2} m³ requested)" });

    // Path B hauling fee: Evola pass-through whenever Jita is an endpoint, fuel+service only otherwise
    var cargoValue = items.Sum(i => i.EstimatedPrice * i.Quantity);
    var haulingFee = evolaRoute
        ? Math.Max(evolaIsk * totalM3 + evolaPct * cargoValue, evolaMin) + (fuelIsk + serviceIsk) * totalM3
        : (fuelIsk + serviceIsk) * totalM3;

    var orderId = await orders.CreateOrderAsync(claims.CharacterId, body.OriginSystem, body.DestinationSystem,
        shopRequested, expedite, expediteFee, body.Notes, items, haulingFee, shopperFeePerItem, shopperFeeMinimum, ct);

    // Notify Discord
    var sFee = shopRequested ? Math.Max(items.Count * shopperFeePerItem, shopperFeeMinimum) : 0;
    _ = discord.NotifyNewOrderAsync(orderId, claims.CharacterName, body.OriginSystem, body.DestinationSystem,
        shopRequested, totalM3, haulingFee, sFee, items.Count);

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
    AuthService auth, OrderRepository orders, UserRepository users, EsiMarketService esi, CancellationToken ct) =>
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

    // Use new origin/destination if provided, otherwise keep existing
    var originSystem = !string.IsNullOrEmpty(body.OriginSystem) ? body.OriginSystem : order.OriginSystem;
    var destinationSystem = !string.IsNullOrEmpty(body.DestinationSystem) ? body.DestinationSystem : order.DestinationSystem;

    var routeError = ValidateRoute(originSystem, destinationSystem);
    if (routeError != null) return Results.BadRequest(new ErrorResponse { Error = routeError });

    var evolaRoute = originSystem == "Jita" || destinationSystem == "Jita";
    var expedite = body.Expedite && evolaRoute;
    var shopRequested = body.ShopRequested && originSystem == "Jita";

    // Read pricing components (Path B — Evola pass-through model)
    var evolaIsk = decimal.Parse(await users.GetConfigAsync("evola_isk_per_m3", ct));
    var evolaPct = decimal.Parse(await users.GetConfigAsync("evola_collateral_pct", ct));
    var evolaMin = decimal.Parse(await users.GetConfigAsync("evola_minimum", ct));
    var fuelIsk = decimal.Parse(await users.GetConfigAsync("fuel_isk_per_m3", ct));
    var serviceIsk = decimal.Parse(await users.GetConfigAsync("service_isk_per_m3", ct));
    var shopperFeePerItem = decimal.Parse(await users.GetConfigAsync("shopper_fee_per_item", ct));
    var shopperFeeMinimum = decimal.Parse(await users.GetConfigAsync("shopper_fee_minimum", ct));
    var expediteFee = expedite ? decimal.Parse(await users.GetConfigAsync("expedite_fee", ct)) : 0;

    var itemInputs = body.Items.Select(i => new OrderItemInput
    {
        TypeId = i.TypeId,
        Quantity = i.Quantity,
        VolumePerUnit = i.VolumePerUnit,
        EstimatedPrice = i.EstimatedPrice
    }).ToList();

    // Backfill any zero-priced line server-side (see POST /api/orders) so an edit can't
    // reintroduce a stray 0 into the collateral math.
    if (evolaRoute)
    {
        foreach (var item in itemInputs.Where(i => i.EstimatedPrice == 0 && i.TypeId > 0))
        {
            var price = await esi.TryGetJitaSellPriceAsync(item.TypeId, ct);
            if (price is > 0) item.EstimatedPrice = price.Value;
        }
    }

    // Path B hauling fee: Evola pass-through whenever Jita is an endpoint, fuel+service only otherwise
    var editTotalM3 = itemInputs.Sum(i => i.VolumePerUnit * i.Quantity);
    var editCargoValue = itemInputs.Sum(i => i.EstimatedPrice * i.Quantity);
    var editHaulingFee = evolaRoute
        ? Math.Max(evolaIsk * editTotalM3 + evolaPct * editCargoValue, evolaMin) + (fuelIsk + serviceIsk) * editTotalM3
        : (fuelIsk + serviceIsk) * editTotalM3;

    await orders.ReplaceOrderItemsAsync(id, originSystem, destinationSystem, shopRequested, expedite, expediteFee, body.Notes, itemInputs, editHaulingFee, shopperFeePerItem, shopperFeeMinimum, ct);
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

// GET /api/config — get fee components (public for display)
app.MapGet("/api/config", async (UserRepository users, CancellationToken ct) =>
{
    var evolaIsk = await users.GetConfigAsync("evola_isk_per_m3", ct);
    var evolaPct = await users.GetConfigAsync("evola_collateral_pct", ct);
    var evolaMin = await users.GetConfigAsync("evola_minimum", ct);
    var fuelIsk = await users.GetConfigAsync("fuel_isk_per_m3", ct);
    var serviceIsk = await users.GetConfigAsync("service_isk_per_m3", ct);
    var shopperFeePerItem = await users.GetConfigAsync("shopper_fee_per_item", ct);
    var shopperFeeMinimum = await users.GetConfigAsync("shopper_fee_minimum", ct);
    var expediteFee = await users.GetConfigAsync("expedite_fee", ct);
    var maxM3 = await users.GetConfigAsync("max_order_m3", ct);
    return Results.Ok(new ConfigResponse
    {
        EvolaIskPerM3 = decimal.Parse(evolaIsk),
        EvolaCollateralPct = decimal.Parse(evolaPct),
        EvolaMinimum = decimal.Parse(evolaMin),
        FuelIskPerM3 = decimal.Parse(fuelIsk),
        ServiceIskPerM3 = decimal.Parse(serviceIsk),
        ShopperFeePerItem = decimal.Parse(shopperFeePerItem),
        ShopperFeeMinimum = decimal.Parse(shopperFeeMinimum),
        ExpediteFee = decimal.Parse(expediteFee),
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
    public decimal EvolaIskPerM3 { get; set; }
    public decimal EvolaCollateralPct { get; set; }
    public decimal EvolaMinimum { get; set; }
    public decimal FuelIskPerM3 { get; set; }
    public decimal ServiceIskPerM3 { get; set; }
    public decimal ShopperFeePerItem { get; set; }
    public decimal ShopperFeeMinimum { get; set; }
    public decimal ExpediteFee { get; set; }
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
