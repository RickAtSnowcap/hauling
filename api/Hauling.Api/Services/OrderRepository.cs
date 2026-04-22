using Npgsql;

namespace Hauling.Api.Services;

public sealed class OrderRepository
{
    private readonly string _connectionString;

    public OrderRepository(string connectionString)
    {
        _connectionString = connectionString;
    }

    public async Task<long> CreateOrderAsync(long characterId, bool shopRequested, List<OrderItemInput> items,
        decimal haulingRate, decimal shopperFeePct, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        decimal totalM3 = 0;
        decimal totalEstimatedIsk = 0;
        foreach (var item in items)
        {
            totalM3 += item.VolumePerUnit * item.Quantity;
            totalEstimatedIsk += item.EstimatedPrice * item.Quantity;
        }

        var haulingFee = totalM3 * haulingRate;
        var shopperFee = shopRequested ? totalEstimatedIsk * (shopperFeePct / 100m) : 0;

        await using var orderCmd = new NpgsqlCommand(@"
            INSERT INTO hauling.orders (character_id, shop_requested, total_m3, total_estimated_isk, hauling_fee, shopper_fee)
            VALUES (@cid, @shop, @m3, @isk, @hfee, @sfee)
            RETURNING order_id", conn, tx);
        orderCmd.Parameters.AddWithValue("cid", characterId);
        orderCmd.Parameters.AddWithValue("shop", shopRequested);
        orderCmd.Parameters.AddWithValue("m3", totalM3);
        orderCmd.Parameters.AddWithValue("isk", totalEstimatedIsk);
        orderCmd.Parameters.AddWithValue("hfee", haulingFee);
        orderCmd.Parameters.AddWithValue("sfee", shopperFee);
        var orderId = (long)(await orderCmd.ExecuteScalarAsync(ct))!;

        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            await using var itemCmd = new NpgsqlCommand(@"
                INSERT INTO hauling.order_items (order_id, type_id, quantity, volume_per_unit, line_m3, estimated_price, sort_order)
                VALUES (@oid, @tid, @qty, @vol, @lm3, @est, @srt)", conn, tx);
            itemCmd.Parameters.AddWithValue("oid", orderId);
            itemCmd.Parameters.AddWithValue("tid", item.TypeId);
            itemCmd.Parameters.AddWithValue("qty", item.Quantity);
            itemCmd.Parameters.AddWithValue("vol", item.VolumePerUnit);
            itemCmd.Parameters.AddWithValue("lm3", item.VolumePerUnit * item.Quantity);
            itemCmd.Parameters.AddWithValue("est", item.EstimatedPrice);
            itemCmd.Parameters.AddWithValue("srt", i);
            await itemCmd.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
        return orderId;
    }

    public async Task<List<OrderSummary>> ListOrdersAsync(long? characterId, int limit, int offset, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        var sql = @"SELECT o.order_id, o.character_id, u.character_name, o.status, o.shop_requested,
                           o.total_m3, o.total_estimated_isk, o.total_actual_isk, o.hauling_fee, o.shopper_fee,
                           o.created_at, o.updated_at, o.assigned_to, h.character_name
                    FROM hauling.orders o
                    JOIN hauling.users u ON o.character_id = u.character_id
                    LEFT JOIN hauling.users h ON o.assigned_to = h.character_id";
        if (characterId.HasValue) sql += " WHERE o.character_id = @cid";
        sql += " ORDER BY o.created_at DESC LIMIT @lim OFFSET @off";

        await using var cmd = new NpgsqlCommand(sql, conn);
        if (characterId.HasValue) cmd.Parameters.AddWithValue("cid", characterId.Value);
        cmd.Parameters.AddWithValue("lim", limit);
        cmd.Parameters.AddWithValue("off", offset);

        var results = new List<OrderSummary>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(new OrderSummary
            {
                OrderId = reader.GetInt64(0),
                CharacterId = reader.GetInt64(1),
                CharacterName = reader.GetString(2),
                Status = reader.GetString(3),
                ShopRequested = reader.GetBoolean(4),
                TotalM3 = reader.GetDecimal(5),
                TotalEstimatedIsk = reader.GetDecimal(6),
                TotalActualIsk = reader.IsDBNull(7) ? null : reader.GetDecimal(7),
                HaulingFee = reader.GetDecimal(8),
                ShopperFee = reader.GetDecimal(9),
                CreatedAt = reader.GetDateTime(10),
                UpdatedAt = reader.GetDateTime(11),
                AssignedTo = reader.IsDBNull(12) ? null : reader.GetInt64(12),
                AssignedToName = reader.IsDBNull(13) ? null : reader.GetString(13)
            });
        }
        return results;
    }

    public async Task<OrderDetail?> GetOrderAsync(long orderId, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var orderCmd = new NpgsqlCommand(@"
            SELECT o.order_id, o.character_id, u.character_name, o.status, o.shop_requested,
                   o.total_m3, o.total_estimated_isk, o.total_actual_isk, o.hauling_fee, o.shopper_fee,
                   o.assigned_to, o.created_at, o.updated_at, h.character_name
            FROM hauling.orders o
            JOIN hauling.users u ON o.character_id = u.character_id
            LEFT JOIN hauling.users h ON o.assigned_to = h.character_id
            WHERE o.order_id = @oid", conn);
        orderCmd.Parameters.AddWithValue("oid", orderId);
        await using var orderReader = await orderCmd.ExecuteReaderAsync(ct);
        if (!await orderReader.ReadAsync(ct)) return null;

        var detail = new OrderDetail
        {
            OrderId = orderReader.GetInt64(0),
            CharacterId = orderReader.GetInt64(1),
            CharacterName = orderReader.GetString(2),
            Status = orderReader.GetString(3),
            ShopRequested = orderReader.GetBoolean(4),
            TotalM3 = orderReader.GetDecimal(5),
            TotalEstimatedIsk = orderReader.GetDecimal(6),
            TotalActualIsk = orderReader.IsDBNull(7) ? null : orderReader.GetDecimal(7),
            HaulingFee = orderReader.GetDecimal(8),
            ShopperFee = orderReader.GetDecimal(9),
            AssignedTo = orderReader.IsDBNull(10) ? null : orderReader.GetInt64(10),
            CreatedAt = orderReader.GetDateTime(11),
            UpdatedAt = orderReader.GetDateTime(12),
            AssignedToName = orderReader.IsDBNull(13) ? null : orderReader.GetString(13)
        };
        await orderReader.CloseAsync();

        await using var itemsCmd = new NpgsqlCommand(@"
            SELECT oi.item_id, oi.type_id, et.type_name, oi.quantity, oi.volume_per_unit, oi.line_m3,
                   oi.estimated_price, oi.actual_price
            FROM hauling.order_items oi
            JOIN hauling.eve_types et ON oi.type_id = et.type_id
            WHERE oi.order_id = @oid
            ORDER BY oi.sort_order", conn);
        itemsCmd.Parameters.AddWithValue("oid", orderId);
        await using var itemsReader = await itemsCmd.ExecuteReaderAsync(ct);
        while (await itemsReader.ReadAsync(ct))
        {
            detail.Items.Add(new OrderItemDetail
            {
                ItemId = itemsReader.GetInt64(0),
                TypeId = itemsReader.GetInt32(1),
                TypeName = itemsReader.GetString(2),
                Quantity = itemsReader.GetInt32(3),
                VolumePerUnit = itemsReader.GetDecimal(4),
                LineM3 = itemsReader.GetDecimal(5),
                EstimatedPrice = itemsReader.GetDecimal(6),
                ActualPrice = itemsReader.IsDBNull(7) ? null : itemsReader.GetDecimal(7)
            });
        }

        return detail;
    }

    public async Task<bool> UpdateStatusAsync(long orderId, string status, long? assignedTo, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(@"
            UPDATE hauling.orders SET status = @st, assigned_to = @asgn, updated_at = now()
            WHERE order_id = @oid", conn);
        cmd.Parameters.AddWithValue("st", status);
        cmd.Parameters.AddWithValue("asgn", assignedTo.HasValue ? assignedTo.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("oid", orderId);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    public async Task<bool> UpdateStatusOnlyAsync(long orderId, string status, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(@"
            UPDATE hauling.orders SET status = @st, updated_at = now()
            WHERE order_id = @oid", conn);
        cmd.Parameters.AddWithValue("st", status);
        cmd.Parameters.AddWithValue("oid", orderId);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    public async Task<long?> UpdateActualPriceAsync(long itemId, decimal actualPrice, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "UPDATE hauling.order_items SET actual_price = @price WHERE item_id = @id RETURNING order_id", conn);
        cmd.Parameters.AddWithValue("price", actualPrice);
        cmd.Parameters.AddWithValue("id", itemId);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result == null ? null : Convert.ToInt64(result);
    }

    public async Task ReplaceOrderItemsAsync(long orderId, bool shopRequested, List<OrderItemInput> items,
        decimal haulingRate, decimal shopperFeePct, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        // Delete existing items
        await using var delCmd = new NpgsqlCommand("DELETE FROM hauling.order_items WHERE order_id = @oid", conn, tx);
        delCmd.Parameters.AddWithValue("oid", orderId);
        await delCmd.ExecuteNonQueryAsync(ct);

        // Insert new items and recalculate
        decimal totalM3 = 0;
        decimal totalEstimatedIsk = 0;
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var lineM3 = item.VolumePerUnit * item.Quantity;
            totalM3 += lineM3;
            totalEstimatedIsk += item.EstimatedPrice * item.Quantity;

            await using var itemCmd = new NpgsqlCommand(@"
                INSERT INTO hauling.order_items (order_id, type_id, quantity, volume_per_unit, line_m3, estimated_price, sort_order)
                VALUES (@oid, @tid, @qty, @vol, @lm3, @est, @srt)", conn, tx);
            itemCmd.Parameters.AddWithValue("oid", orderId);
            itemCmd.Parameters.AddWithValue("tid", item.TypeId);
            itemCmd.Parameters.AddWithValue("qty", item.Quantity);
            itemCmd.Parameters.AddWithValue("vol", item.VolumePerUnit);
            itemCmd.Parameters.AddWithValue("lm3", lineM3);
            itemCmd.Parameters.AddWithValue("est", item.EstimatedPrice);
            itemCmd.Parameters.AddWithValue("srt", i);
            await itemCmd.ExecuteNonQueryAsync(ct);
        }

        var haulingFee = totalM3 * haulingRate;
        var shopperFee = shopRequested ? totalEstimatedIsk * (shopperFeePct / 100m) : 0;

        await using var updateCmd = new NpgsqlCommand(@"
            UPDATE hauling.orders SET shop_requested = @shop, total_m3 = @m3, total_estimated_isk = @isk,
                hauling_fee = @hfee, shopper_fee = @sfee, updated_at = now()
            WHERE order_id = @oid", conn, tx);
        updateCmd.Parameters.AddWithValue("shop", shopRequested);
        updateCmd.Parameters.AddWithValue("m3", totalM3);
        updateCmd.Parameters.AddWithValue("isk", totalEstimatedIsk);
        updateCmd.Parameters.AddWithValue("hfee", haulingFee);
        updateCmd.Parameters.AddWithValue("sfee", shopperFee);
        updateCmd.Parameters.AddWithValue("oid", orderId);
        await updateCmd.ExecuteNonQueryAsync(ct);

        await tx.CommitAsync(ct);
    }

    public async Task<bool> AssignHaulerAsync(long orderId, long characterId, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "UPDATE hauling.orders SET assigned_to = @cid, updated_at = now() WHERE order_id = @oid", conn);
        cmd.Parameters.AddWithValue("cid", characterId);
        cmd.Parameters.AddWithValue("oid", orderId);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    public async Task<bool> DeleteOrderAsync(long orderId, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        await using var itemsCmd = new NpgsqlCommand("DELETE FROM hauling.order_items WHERE order_id = @oid", conn, tx);
        itemsCmd.Parameters.AddWithValue("oid", orderId);
        await itemsCmd.ExecuteNonQueryAsync(ct);

        await using var orderCmd = new NpgsqlCommand("DELETE FROM hauling.orders WHERE order_id = @oid", conn, tx);
        orderCmd.Parameters.AddWithValue("oid", orderId);
        var deleted = await orderCmd.ExecuteNonQueryAsync(ct) > 0;

        await tx.CommitAsync(ct);
        return deleted;
    }

    public async Task RecalcTotalActualAsync(long orderId, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(@"
            UPDATE hauling.orders SET total_actual_isk = (
                SELECT SUM(actual_price * quantity) FROM hauling.order_items
                WHERE order_id = @oid AND actual_price IS NOT NULL
            ), updated_at = now() WHERE order_id = @oid", conn);
        cmd.Parameters.AddWithValue("oid", orderId);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}

public sealed class OrderItemInput
{
    public int TypeId { get; set; }
    public int Quantity { get; set; }
    public decimal VolumePerUnit { get; set; }
    public decimal EstimatedPrice { get; set; }
}

public class OrderSummary
{
    public long OrderId { get; set; }
    public long CharacterId { get; set; }
    public string CharacterName { get; set; } = "";
    public string Status { get; set; } = "";
    public bool ShopRequested { get; set; }
    public decimal TotalM3 { get; set; }
    public decimal TotalEstimatedIsk { get; set; }
    public decimal? TotalActualIsk { get; set; }
    public decimal HaulingFee { get; set; }
    public decimal ShopperFee { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public long? AssignedTo { get; set; }
    public string? AssignedToName { get; set; }
}

public sealed class OrderDetail : OrderSummary
{
    public List<OrderItemDetail> Items { get; set; } = new();
}

public sealed class OrderItemDetail
{
    public long ItemId { get; set; }
    public int TypeId { get; set; }
    public string TypeName { get; set; } = "";
    public int Quantity { get; set; }
    public decimal VolumePerUnit { get; set; }
    public decimal LineM3 { get; set; }
    public decimal EstimatedPrice { get; set; }
    public decimal? ActualPrice { get; set; }
}
