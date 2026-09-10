using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Hauling.Api.Services;

public sealed class EsiMarketService
{
    private readonly HttpClient _http;
    private readonly ILogger<EsiMarketService> _logger;
    private const long TheForgeRegionId = 10000002; // Jita's region
    private const int MaxAttempts = 3;
    private const int IsotopeCacheTtlMinutes = 15;
    private const decimal FallbackIsotopePrice = 640m;

    private decimal? _cachedIsotopePrice;
    private DateTime _isotopeCacheExpiry = DateTime.MinValue;
    private readonly SemaphoreSlim _isotopeLock = new(1, 1);

    public EsiMarketService(ILogger<EsiMarketService> logger)
    {
        _logger = logger;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        // ESI requires a descriptive User-Agent with contact info. A blank/absent UA gets
        // throttled hardest — that is what silently zeroed the last item of a large order.
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "AMA-Hauling/1.0 (+https://hauling.angry.no; maintainer: Bendigo Xana)");
    }

    public async Task<decimal> GetIsotopePriceAsync(int typeId, CancellationToken ct)
    {
        if (_isotopeCacheExpiry > DateTime.UtcNow && _cachedIsotopePrice.HasValue)
            return _cachedIsotopePrice.Value;

        await _isotopeLock.WaitAsync(ct);
        try
        {
            if (_isotopeCacheExpiry > DateTime.UtcNow && _cachedIsotopePrice.HasValue)
                return _cachedIsotopePrice.Value;

            var price = await TryGetJitaSellPriceAsync(typeId, ct);
            if (price.HasValue && price.Value > 0)
            {
                _cachedIsotopePrice = price.Value;
                _isotopeCacheExpiry = DateTime.UtcNow.AddMinutes(IsotopeCacheTtlMinutes);
                _logger.LogInformation("Isotope price cached: {Price} ISK (type {TypeId})", price.Value, typeId);
                return price.Value;
            }

            if (_cachedIsotopePrice.HasValue)
            {
                _logger.LogWarning("ESI isotope lookup failed, using last known price: {Price} ISK", _cachedIsotopePrice.Value);
                return _cachedIsotopePrice.Value;
            }

            _logger.LogWarning("ESI isotope lookup failed with no prior cache, using fallback: {Price} ISK", FallbackIsotopePrice);
            return FallbackIsotopePrice;
        }
        finally
        {
            _isotopeLock.Release();
        }
    }

    // Lowest The Forge (Jita region) sell price for a type.
    // Returns null when the lookup FAILED (throttled / transient error / exhausted retries) so
    // callers can tell "we don't know" apart from a genuine 0 (ESI returned no sell orders).
    public async Task<decimal?> TryGetJitaSellPriceAsync(int typeId, CancellationToken ct)
    {
        var url = $"https://esi.evetech.net/latest/markets/{TheForgeRegionId}/orders/?type_id={typeId}&order_type=sell&datasource=tranquility";

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            try
            {
                using var resp = await _http.GetAsync(url, ct);
                if (resp.IsSuccessStatusCode)
                {
                    using var doc = await JsonDocument.ParseAsync(
                        await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
                    decimal lowest = decimal.MaxValue;
                    foreach (var order in doc.RootElement.EnumerateArray())
                    {
                        var price = order.GetProperty("price").GetDecimal();
                        if (price < lowest) lowest = price;
                    }
                    return lowest == decimal.MaxValue ? 0m : lowest;
                }

                // 420 (ESI error-limited) / 429 / 5xx are transient — back off and retry.
                // Anything else (404 etc.) is fatal for this type; surface as "unknown", not 0.
                var status = (int)resp.StatusCode;
                var transient = status is 420 or 429 || status >= 500;
                _logger.LogWarning(
                    "ESI market lookup for type {TypeId} returned HTTP {Status} (attempt {Attempt}/{Max}){Retry}",
                    typeId, status, attempt, MaxAttempts, transient ? " — will retry" : "");
                if (!transient) return null;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex,
                    "ESI market lookup for type {TypeId} threw (attempt {Attempt}/{Max})",
                    typeId, attempt, MaxAttempts);
            }

            if (attempt < MaxAttempts)
                await Task.Delay(TimeSpan.FromMilliseconds(300 * attempt), ct); // linear backoff: 300ms, 600ms
        }

        _logger.LogWarning("ESI market lookup for type {TypeId} failed after {Max} attempts", typeId, MaxAttempts);
        return null;
    }
}
