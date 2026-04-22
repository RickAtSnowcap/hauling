using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Hauling.Api.Services;

public sealed class AuthService
{
    private readonly byte[] _key;

    public AuthService(string jwtKey)
    {
        _key = Encoding.UTF8.GetBytes(jwtKey);
    }

    public string CreateToken(long characterId, string characterName, string role)
    {
        var header = Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"alg\":\"HS256\",\"typ\":\"JWT\"}"))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

        var exp = DateTimeOffset.UtcNow.AddHours(24).ToUnixTimeSeconds();
        var payloadJson = $"{{\"sub\":\"{characterId}\",\"name\":\"{EscapeJson(characterName)}\",\"role\":\"{role}\",\"exp\":{exp}}}";
        var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(payloadJson))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

        var sigInput = Encoding.UTF8.GetBytes($"{header}.{payload}");
        using var hmac = new HMACSHA256(_key);
        var sig = Convert.ToBase64String(hmac.ComputeHash(sigInput))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

        return $"{header}.{payload}.{sig}";
    }

    public TokenClaims? ValidateToken(string token)
    {
        var parts = token.Split('.');
        if (parts.Length != 3) return null;

        // Verify signature
        var sigInput = Encoding.UTF8.GetBytes($"{parts[0]}.{parts[1]}");
        using var hmac = new HMACSHA256(_key);
        var expectedSig = Convert.ToBase64String(hmac.ComputeHash(sigInput))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

        if (parts[2] != expectedSig) return null;

        // Decode payload
        var payload = parts[1].Replace('-', '+').Replace('_', '/');
        switch (payload.Length % 4)
        {
            case 2: payload += "=="; break;
            case 3: payload += "="; break;
        }

        try
        {
            using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(payload)));
            var exp = doc.RootElement.GetProperty("exp").GetInt64();
            if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() > exp) return null;

            return new TokenClaims
            {
                CharacterId = long.Parse(doc.RootElement.GetProperty("sub").GetString() ?? "0"),
                CharacterName = doc.RootElement.GetProperty("name").GetString() ?? "",
                Role = doc.RootElement.GetProperty("role").GetString() ?? "member"
            };
        }
        catch { return null; }
    }

    private static string EscapeJson(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
}

public sealed class TokenClaims
{
    public long CharacterId { get; set; }
    public string CharacterName { get; set; } = "";
    public string Role { get; set; } = "member";
}
