using System.Text.Json;

namespace Hauling.Api.Services;

public sealed class EsiMarketService
{
    private readonly HttpClient _http;
    private const long TheForgeRegionId = 10000002; // Jita's region

    public EsiMarketService()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    public async Task<decimal> GetJitaSellPriceAsync(int typeId, CancellationToken ct)
    {
        try
        {
            var resp = await _http.GetAsync(
                $"https://esi.evetech.net/latest/markets/{TheForgeRegionId}/orders/?type_id={typeId}&order_type=sell&datasource=tranquility", ct);
            if (!resp.IsSuccessStatusCode) return 0;

            using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            decimal lowestPrice = decimal.MaxValue;
            foreach (var order in doc.RootElement.EnumerateArray())
            {
                // Only consider orders in Jita (station 60003760) or any Forge sell order
                var price = order.GetProperty("price").GetDecimal();
                if (price < lowestPrice) lowestPrice = price;
            }
            return lowestPrice == decimal.MaxValue ? 0 : lowestPrice;
        }
        catch
        {
            return 0;
        }
    }
}
