using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Hauling.Api.Services;

public sealed class EveSsoService
{
    private readonly string _clientId;
    private readonly string _clientSecret;
    private readonly string _callbackUrl;
    private readonly HttpClient _http;

    public EveSsoService(string clientId, string clientSecret, string callbackUrl)
    {
        _clientId = clientId;
        _clientSecret = clientSecret;
        _callbackUrl = callbackUrl;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    public string GetAuthorizeUrl(string state) =>
        $"https://login.eveonline.com/v2/oauth/authorize?response_type=code&redirect_uri={Uri.EscapeDataString(_callbackUrl)}&client_id={_clientId}&scope=publicData&state={state}";

    public async Task<EveCharacterInfo?> ExchangeCodeAsync(string code, CancellationToken ct)
    {
        // Exchange code for token
        var basicAuth = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_clientId}:{_clientSecret}"));
        var tokenReq = new HttpRequestMessage(HttpMethod.Post, "https://login.eveonline.com/v2/oauth/token");
        tokenReq.Headers.Authorization = new AuthenticationHeaderValue("Basic", basicAuth);
        tokenReq.Content = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("grant_type", "authorization_code"),
            new KeyValuePair<string, string>("code", code)
        });

        var tokenResp = await _http.SendAsync(tokenReq, ct);
        if (!tokenResp.IsSuccessStatusCode) return null;

        using var tokenDoc = await JsonDocument.ParseAsync(await tokenResp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        var accessToken = tokenDoc.RootElement.GetProperty("access_token").GetString() ?? "";

        // Decode JWT to get character ID (JWT is base64url encoded, middle segment has the claims)
        var parts = accessToken.Split('.');
        if (parts.Length < 2) return null;

        var payload = parts[1];
        // Fix base64url padding
        payload = payload.Replace('-', '+').Replace('_', '/');
        switch (payload.Length % 4)
        {
            case 2: payload += "=="; break;
            case 3: payload += "="; break;
        }
        var claimsJson = Encoding.UTF8.GetString(Convert.FromBase64String(payload));
        using var claimsDoc = JsonDocument.Parse(claimsJson);
        var sub = claimsDoc.RootElement.GetProperty("sub").GetString() ?? "";
        // sub format: "CHARACTER:EVE:{character_id}"
        var subParts = sub.Split(':');
        if (subParts.Length < 3 || !long.TryParse(subParts[2], out var characterId)) return null;

        var characterName = claimsDoc.RootElement.GetProperty("name").GetString() ?? "";

        // Get character details from ESI for corp/alliance
        var esiResp = await _http.GetAsync($"https://esi.evetech.net/latest/characters/{characterId}/?datasource=tranquility", ct);
        if (!esiResp.IsSuccessStatusCode) return null;

        using var esiDoc = await JsonDocument.ParseAsync(await esiResp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        var corpId = esiDoc.RootElement.GetProperty("corporation_id").GetInt64();
        long? allianceId = esiDoc.RootElement.TryGetProperty("alliance_id", out var alProp) ? alProp.GetInt64() : null;

        return new EveCharacterInfo
        {
            CharacterId = characterId,
            CharacterName = characterName,
            CorporationId = corpId,
            AllianceId = allianceId
        };
    }
}

public sealed class EveCharacterInfo
{
    public long CharacterId { get; set; }
    public string CharacterName { get; set; } = "";
    public long CorporationId { get; set; }
    public long? AllianceId { get; set; }
}
