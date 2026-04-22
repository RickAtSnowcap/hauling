using Npgsql;

namespace Hauling.Api.Services;

public sealed class ItemRepository
{
    private readonly string _connectionString;

    public ItemRepository(string connectionString)
    {
        _connectionString = connectionString;
    }

    public async Task<List<ItemResult>> SearchAsync(string query, int limit, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "SELECT type_id, type_name, COALESCE(packaged_volume, volume) FROM hauling.eve_types WHERE type_name ILIKE @q ORDER BY type_name LIMIT @lim", conn);
        cmd.Parameters.AddWithValue("q", $"%{query}%");
        cmd.Parameters.AddWithValue("lim", limit);

        var results = new List<ItemResult>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(new ItemResult
            {
                TypeId = reader.GetInt32(0),
                TypeName = reader.GetString(1),
                Volume = reader.GetDecimal(2)
            });
        }
        return results;
    }

    public async Task<List<ItemResult>> MatchNamesAsync(List<string> names, CancellationToken ct)
    {
        if (names.Count == 0) return new();
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        // Use ANY for batch matching - exact case-insensitive match
        await using var cmd = new NpgsqlCommand(
            "SELECT type_id, type_name, COALESCE(packaged_volume, volume) FROM hauling.eve_types WHERE LOWER(type_name) = ANY(@names)", conn);
        cmd.Parameters.AddWithValue("names", names.Select(n => n.ToLowerInvariant()).ToArray());

        var results = new List<ItemResult>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(new ItemResult
            {
                TypeId = reader.GetInt32(0),
                TypeName = reader.GetString(1),
                Volume = reader.GetDecimal(2)
            });
        }
        return results;
    }

    public async Task<ItemResult?> GetByIdAsync(int typeId, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "SELECT type_id, type_name, COALESCE(packaged_volume, volume) FROM hauling.eve_types WHERE type_id = @id", conn);
        cmd.Parameters.AddWithValue("id", typeId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return new ItemResult
        {
            TypeId = reader.GetInt32(0),
            TypeName = reader.GetString(1),
            Volume = reader.GetDecimal(2)
        };
    }
}

public sealed class ItemResult
{
    public int TypeId { get; set; }
    public string TypeName { get; set; } = "";
    public decimal Volume { get; set; }
}
