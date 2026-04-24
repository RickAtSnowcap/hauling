using System.Text;

namespace Hauling.Api.Services;

public sealed class DiscordNotifier
{
    private readonly string _webhookUrl;
    private readonly HttpClient _http;

    public DiscordNotifier(string webhookUrl)
    {
        _webhookUrl = webhookUrl;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
    }

    public async Task NotifyNewOrderAsync(long orderId, string characterName, string origin, string destination,
        bool shopRequested, decimal totalM3, decimal haulingFee, decimal shopperFee, int itemCount)
    {
        if (string.IsNullOrEmpty(_webhookUrl)) return;

        var mode = shopRequested ? "Shop + Haul" : "Haul Only";
        var feeTotal = haulingFee + shopperFee;

        var description = $"**Order #{orderId}** placed by **{characterName}**\n"
            + $"Route: {origin} → {destination}\n"
            + $"Type: {mode}\n"
            + $"Items: {itemCount} | Volume: {totalM3:N0} m³\n"
            + $"Hauling Fee: {haulingFee:N0} ISK";

        if (shopRequested)
            description += $"\nShopper Fee: {shopperFee:N0} ISK\nTotal: {feeTotal:N0} ISK";

        var json = "{\"embeds\":[{"
            + "\"title\":\"New Hauling Order\","
            + "\"description\":\"" + EscapeJson(description) + "\","
            + "\"color\":15158332,"
            + "\"footer\":{\"text\":\"Angry Hauling\"}"
            + "}]}";

        try
        {
            await _http.PostAsync(_webhookUrl, new StringContent(json, Encoding.UTF8, "application/json"));
        }
        catch
        {
            // Don't let notification failures break order creation
        }
    }

    private static string EscapeJson(string s) => s
        .Replace("\\", "\\\\")
        .Replace("\"", "\\\"")
        .Replace("\n", "\\n");
}
