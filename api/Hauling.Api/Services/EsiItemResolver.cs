using System.Text;
using System.Text.Json;

namespace Hauling.Api.Services;

public sealed class EsiItemResolver
{
    private readonly HttpClient _http;

    public EsiItemResolver()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
    }

    /// <summary>
    /// Resolve item names to type IDs via ESI POST /universe/ids/
    /// </summary>
    public async Task<List<(int TypeId, string Name)>> ResolveNamesAsync(List<string> names, CancellationToken ct)
    {
        if (names.Count == 0) return new();

        var json = "[" + string.Join(",", names.Select(n => "\"" + EscapeJson(n) + "\"")) + "]";
        var resp = await _http.PostAsync(
            "https://esi.evetech.net/latest/universe/ids/?datasource=tranquility",
            new StringContent(json, Encoding.UTF8, "application/json"), ct);

        if (!resp.IsSuccessStatusCode) return new();

        using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        var results = new List<(int, string)>();

        if (doc.RootElement.TryGetProperty("inventory_types", out var types))
        {
            foreach (var item in types.EnumerateArray())
            {
                var id = item.GetProperty("id").GetInt32();
                var name = item.GetProperty("name").GetString() ?? "";
                results.Add((id, name));
            }
        }

        return results;
    }

    /// <summary>
    /// Get item details (volume, packaged_volume) from ESI GET /universe/types/{id}
    /// </summary>
    public async Task<EsiTypeInfo?> GetTypeInfoAsync(int typeId, CancellationToken ct)
    {
        try
        {
            var resp = await _http.GetAsync(
                $"https://esi.evetech.net/latest/universe/types/{typeId}/?datasource=tranquility", ct);
            if (!resp.IsSuccessStatusCode) return null;

            using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            var root = doc.RootElement;

            return new EsiTypeInfo
            {
                TypeId = typeId,
                TypeName = root.GetProperty("name").GetString() ?? "",
                Volume = root.TryGetProperty("volume", out var vol) ? (decimal)vol.GetDouble() : 0,
                PackagedVolume = root.TryGetProperty("packaged_volume", out var pv) ? (decimal)pv.GetDouble() : null
            };
        }
        catch
        {
            return null;
        }
    }

    private static string EscapeJson(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
}

public sealed class EsiTypeInfo
{
    public int TypeId { get; set; }
    public string TypeName { get; set; } = "";
    public decimal Volume { get; set; }
    public decimal? PackagedVolume { get; set; }
}
