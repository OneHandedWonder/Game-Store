using System.ComponentModel.DataAnnotations;
using Game_Store_API.Auth;
using Game_Store_API.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Npgsql;

namespace Game_Store_API.Controllers;

[ApiController, Authorize(Roles = AppRoles.Staff)]
[Route("api/staff/orders")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class StaffOrdersController(NpgsqlDataSource source, IDataProtectionProvider protection) : ControllerBase
{
    [HttpGet("analytics")]
    public async Task<ActionResult<OrderAnalytics>> Analytics([Range(7, 90)] int days = 30, CancellationToken cancellationToken = default)
    {
        var daily = new List<DailyOrders>();
        await using (var command = source.CreateCommand("""
            WITH dates AS (
                SELECT ((now() AT TIME ZONE 'Europe/Copenhagen')::date - n)::date AS day FROM generate_series(0, $1 - 1) n
            ), totals AS (
                SELECT (created_at AT TIME ZONE 'Europe/Copenhagen')::date AS day, count(*) AS orders, sum(total) AS sales
                FROM orders WHERE created_at >= (((now() AT TIME ZONE 'Europe/Copenhagen')::date - ($1 - 1))::timestamp AT TIME ZONE 'Europe/Copenhagen')
                GROUP BY 1
            ) SELECT d.day, coalesce(t.orders,0), coalesce(t.sales,0) FROM dates d LEFT JOIN totals t USING(day) ORDER BY d.day
            """))
        {
            command.Parameters.AddWithValue(days);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) daily.Add(new(reader.GetFieldValue<DateOnly>(0), reader.GetInt64(1), reader.GetDecimal(2)));
        }
        var products = new List<TopProduct>();
        await using (var command = source.CreateCommand("""
            SELECT i.product_id, (array_agg(i.name ORDER BY o.created_at DESC, o.id DESC))[1], sum(i.quantity), sum(i.quantity * i.price)
            FROM order_items i JOIN orders o ON o.id = i.order_id
            WHERE o.created_at >= (((now() AT TIME ZONE 'Europe/Copenhagen')::date - ($1 - 1))::timestamp AT TIME ZONE 'Europe/Copenhagen')
            AND (o.created_at AT TIME ZONE 'Europe/Copenhagen')::date <= (now() AT TIME ZONE 'Europe/Copenhagen')::date
            GROUP BY i.product_id ORDER BY sum(i.quantity) DESC, i.product_id LIMIT 5
            """))
        {
            command.Parameters.AddWithValue(days);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) products.Add(new(reader.GetGuid(0), reader.GetString(1), reader.GetInt64(2), reader.GetDecimal(3)));
        }
        return Ok(new OrderAnalytics(daily, products));
    }
    [HttpGet("metrics")]
    public async Task<ActionResult<ManagementMetrics>> Metrics(CancellationToken cancellationToken)
    {
        await using var command = source.CreateCommand("""
            SELECT count(*), count(*) FILTER (WHERE fulfillment_status = 'fulfilled'),
                count(*) FILTER (WHERE fulfillment_status = 'unfulfilled'), coalesce(sum(total),0), coalesce(avg(total),0),
                count(*) FILTER (WHERE (created_at AT TIME ZONE 'Europe/Copenhagen')::date = (now() AT TIME ZONE 'Europe/Copenhagen')::date),
                count(DISTINCT user_id), (SELECT coalesce(sum(quantity),0) FROM order_items),
                (SELECT count(*) FROM products WHERE is_active),
                (SELECT count(*) FROM products WHERE is_active AND stock <= 5)
            FROM orders
            """);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) throw new InvalidOperationException("Order metrics returned no row.");
        return Ok(new ManagementMetrics(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetDecimal(3),
            reader.GetDecimal(4), reader.GetInt64(5), reader.GetInt64(6), reader.GetInt64(7), reader.GetInt64(8), reader.GetInt64(9)));
    }

    [HttpPut("{id:guid}/fulfillment")]
    [RequestSizeLimit(4096)]
    public async Task<IActionResult> SetFulfillment(Guid id, FulfillmentRequest request, CancellationToken cancellationToken)
    {
        await using var command = source.CreateCommand("""
            UPDATE orders SET fulfillment_status = $2, fulfillment_version = gen_random_uuid()
            WHERE id = $1 AND fulfillment_version = $3 RETURNING fulfillment_version
            """);
        command.Parameters.AddWithValue(id);
        command.Parameters.AddWithValue(request.Status);
        command.Parameters.AddWithValue(request.Version);
        if (await command.ExecuteScalarAsync(cancellationToken) is Guid version)
            return Ok(new { fulfillmentStatus = request.Status, fulfillmentVersion = version });
        await using var exists = source.CreateCommand("SELECT EXISTS (SELECT 1 FROM orders WHERE id = $1)");
        exists.Parameters.AddWithValue(id);
        return await exists.ExecuteScalarAsync(cancellationToken) is true
            ? Conflict(new { message = "This order changed. Reload its details before updating fulfillment." }) : NotFound();
    }

    [HttpGet]
    public async Task<ActionResult<List<StaffOrderSummary>>> List([Range(1, 1_000_000)] int page = 1,
        CancellationToken cancellationToken = default)
    {
        await using var command = source.CreateCommand("""
            SELECT o.id, o.created_at, o.total, o.currency, o.payment_status, u.email, o.fulfillment_status, o.fulfillment_version
            FROM orders o JOIN users u ON u.id = o.user_id
            ORDER BY o.created_at DESC, o.id DESC LIMIT 20 OFFSET $1
            """);
        command.Parameters.AddWithValue((page - 1) * 20);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var orders = new List<StaffOrderSummary>();
        while (await reader.ReadAsync(cancellationToken)) orders.Add(Read(reader));
        return Ok(orders);
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<StaffOrderDetail>> Get(Guid id, CancellationToken cancellationToken)
    {
        await using var command = source.CreateCommand("""
            SELECT o.id, o.created_at, o.total, o.currency, o.payment_status, u.email, o.fulfillment_status, o.fulfillment_version, o.protected_delivery_address
            FROM orders o JOIN users u ON u.id = o.user_id WHERE o.id = $1
            """);
        command.Parameters.AddWithValue(id);
        StaffOrderSummary summary;
        DeliveryAddress? address;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken)) return NotFound();
            summary = Read(reader);
            address = DeliveryAddress.Unprotect(reader.IsDBNull(8) ? null : reader.GetString(8), protection);
        }
        await using var lines = source.CreateCommand("SELECT product_id, name, price, quantity FROM order_items WHERE order_id = $1 ORDER BY name, product_id");
        lines.Parameters.AddWithValue(id);
        await using var itemReader = await lines.ExecuteReaderAsync(cancellationToken);
        var items = new List<OrderItem>();
        while (await itemReader.ReadAsync(cancellationToken))
            items.Add(new(itemReader.GetGuid(0), itemReader.GetString(1), itemReader.GetDecimal(2), itemReader.GetInt32(3)));
        return Ok(new StaffOrderDetail(summary.Id, summary.CreatedAt, summary.Total, summary.Currency,
            summary.PaymentStatus, summary.CustomerEmail, summary.FulfillmentStatus, summary.FulfillmentVersion, items, address));
    }

    private static StaffOrderSummary Read(NpgsqlDataReader reader) =>
        new(reader.GetGuid(0), reader.GetDateTime(1), reader.GetDecimal(2), reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetGuid(7));
}

public sealed record StaffOrderSummary(Guid Id, DateTime CreatedAt, decimal Total, string Currency, string PaymentStatus, string CustomerEmail, string FulfillmentStatus, Guid FulfillmentVersion);
public sealed record StaffOrderDetail(Guid Id, DateTime CreatedAt, decimal Total, string Currency, string PaymentStatus, string CustomerEmail, string FulfillmentStatus, Guid FulfillmentVersion, List<OrderItem> Items, DeliveryAddress? DeliveryAddress);
public sealed record DailyOrders(DateOnly Date, long Orders, decimal Sales);
public sealed record TopProduct(Guid ProductId, string Name, long Units, decimal Sales);
public sealed record OrderAnalytics(List<DailyOrders> Daily, List<TopProduct> TopProducts);
public sealed record FulfillmentRequest([Required, RegularExpression("fulfilled|unfulfilled")] string Status, Guid Version);
public sealed record ManagementMetrics(long TotalOrders, long FulfilledOrders, long UnfulfilledOrders, decimal TotalSales,
    decimal AverageOrderValue, long OrdersToday, long OrderingCustomers, long UnitsOrdered, long ActiveProducts, long LowStockProducts);
