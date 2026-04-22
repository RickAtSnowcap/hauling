using Npgsql;

namespace Hauling.Api.Services;

public sealed class UserRepository
{
    private readonly string _connectionString;

    public UserRepository(string connectionString)
    {
        _connectionString = connectionString;
    }

    public async Task<UserRecord?> GetUserAsync(long characterId, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "SELECT character_id, character_name, corporation_id, alliance_id, role FROM hauling.users WHERE character_id = @id", conn);
        cmd.Parameters.AddWithValue("id", characterId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return new UserRecord
        {
            CharacterId = reader.GetInt64(0),
            CharacterName = reader.GetString(1),
            CorporationId = reader.GetInt64(2),
            AllianceId = reader.IsDBNull(3) ? null : reader.GetInt64(3),
            Role = reader.GetString(4)
        };
    }

    public async Task<UserRecord> UpsertUserAsync(long characterId, string characterName, long corporationId, long? allianceId, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(@"
            INSERT INTO hauling.users (character_id, character_name, corporation_id, alliance_id)
            VALUES (@cid, @name, @corp, @alliance)
            ON CONFLICT (character_id) DO UPDATE SET
                character_name = EXCLUDED.character_name,
                corporation_id = EXCLUDED.corporation_id,
                alliance_id = EXCLUDED.alliance_id,
                last_login = now()
            RETURNING character_id, character_name, corporation_id, alliance_id, role", conn);
        cmd.Parameters.AddWithValue("cid", characterId);
        cmd.Parameters.AddWithValue("name", characterName);
        cmd.Parameters.AddWithValue("corp", corporationId);
        cmd.Parameters.AddWithValue("alliance", allianceId.HasValue ? allianceId.Value : DBNull.Value);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return new UserRecord
        {
            CharacterId = reader.GetInt64(0),
            CharacterName = reader.GetString(1),
            CorporationId = reader.GetInt64(2),
            AllianceId = reader.IsDBNull(3) ? null : reader.GetInt64(3),
            Role = reader.GetString(4)
        };
    }

    public async Task<string> GetConfigAsync(string key, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("SELECT value FROM hauling.config WHERE key = @k", conn);
        cmd.Parameters.AddWithValue("k", key);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result?.ToString() ?? "";
    }
}

public sealed class UserRecord
{
    public long CharacterId { get; set; }
    public string CharacterName { get; set; } = "";
    public long CorporationId { get; set; }
    public long? AllianceId { get; set; }
    public string Role { get; set; } = "member";
}
