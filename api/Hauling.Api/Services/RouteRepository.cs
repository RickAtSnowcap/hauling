using Npgsql;

namespace Hauling.Api.Services;

public sealed class RouteRepository
{
    private readonly string _connectionString;

    public RouteRepository(string connectionString)
    {
        _connectionString = connectionString;
    }

    public async Task<RouteInfo?> GetRouteAsync(string origin, string destination, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(@"
            SELECT origin, destination, round_trip_isotopes, has_pushx, allows_shopping,
                   label_origin, label_destination
            FROM hauling.routes
            WHERE origin = @o AND destination = @d", conn);
        cmd.Parameters.AddWithValue("o", origin);
        cmd.Parameters.AddWithValue("d", destination);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return ReadRoute(reader);
    }

    public async Task<List<RouteInfo>> GetAllRoutesAsync(CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(@"
            SELECT origin, destination, round_trip_isotopes, has_pushx, allows_shopping,
                   label_origin, label_destination
            FROM hauling.routes
            ORDER BY origin, destination", conn);

        var results = new List<RouteInfo>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            results.Add(ReadRoute(reader));
        return results;
    }

    private static RouteInfo ReadRoute(NpgsqlDataReader reader) => new()
    {
        Origin = reader.GetString(0),
        Destination = reader.GetString(1),
        RoundTripIsotopes = reader.GetInt32(2),
        HasPushx = reader.GetBoolean(3),
        AllowsShopping = reader.GetBoolean(4),
        LabelOrigin = reader.IsDBNull(5) ? null : reader.GetString(5),
        LabelDestination = reader.IsDBNull(6) ? null : reader.GetString(6)
    };
}

public sealed class RouteInfo
{
    public string Origin { get; set; } = "";
    public string Destination { get; set; } = "";
    public int RoundTripIsotopes { get; set; }
    public bool HasPushx { get; set; }
    public bool AllowsShopping { get; set; }
    public string? LabelOrigin { get; set; }
    public string? LabelDestination { get; set; }
}
