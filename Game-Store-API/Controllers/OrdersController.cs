using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using Game_Store_API.Auth;
using Game_Store_API.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Npgsql;

namespace Game_Store_API.Controllers;

[ApiController, Authorize]
[Route("api/orders")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[RequestSizeLimit(32_768)]
public sealed class OrdersController(NpgsqlDataSource source, IDataProtectionProvider protection) : ControllerBase
{
    private Guid UserId => Guid.Parse(User.FindFirstValue("sub")!);

    [HttpGet]
    public async Task<ActionResult<List<OrderSummary>>> List([Range(1, 1_000_000)] int page = 1,
        CancellationToken cancellationToken = default)
    {
        await using var command = source.CreateCommand("""
            SELECT id, created_at, total, currency, payment_status FROM orders
            WHERE user_id = $1 ORDER BY created_at DESC, id DESC LIMIT 20 OFFSET $2
            """);
        command.Parameters.AddWithValue(UserId);
        command.Parameters.AddWithValue((page - 1) * 20);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var orders = new List<OrderSummary>();
        while (await reader.ReadAsync(cancellationToken)) orders.Add(ReadSummary(reader));
        return Ok(orders);
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<OrderDetail>> Get(Guid id, CancellationToken cancellationToken)
    {
        await using var command = source.CreateCommand("SELECT id, created_at, total, currency, payment_status, protected_delivery_address FROM orders WHERE id = $1 AND user_id = $2");
        command.Parameters.AddWithValue(id);
        command.Parameters.AddWithValue(UserId);
        OrderSummary summary;
        DeliveryAddress? address;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken)) return NotFound();
            summary = ReadSummary(reader);
            address = DeliveryAddress.Unprotect(reader.IsDBNull(5) ? null : reader.GetString(5), protection);
        }
        await using var lines = source.CreateCommand("""
            SELECT i.product_id, i.name, i.price, i.quantity FROM order_items i
            JOIN orders o ON o.id = i.order_id WHERE o.id = $1 AND o.user_id = $2 ORDER BY i.name, i.product_id
            """);
        lines.Parameters.AddWithValue(id);
        lines.Parameters.AddWithValue(UserId);
        await using var itemReader = await lines.ExecuteReaderAsync(cancellationToken);
        var items = new List<OrderItem>();
        while (await itemReader.ReadAsync(cancellationToken))
            items.Add(new(itemReader.GetGuid(0), itemReader.GetString(1), itemReader.GetDecimal(2), itemReader.GetInt32(3)));
        return Ok(new OrderDetail(summary.Id, summary.CreatedAt, summary.Total, summary.Currency, summary.PaymentStatus, items, address));
    }

    [HttpPost("checkout")]
    [EnableRateLimiting("checkout")]
    public async Task<IActionResult> Checkout(CheckoutRequest request, CancellationToken cancellationToken)
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        // All cart writes take this lock first so one account's cart cannot change during checkout.
        string email;
        await using (var user = new NpgsqlCommand("SELECT security_version, email FROM users WHERE id = $1 FOR UPDATE", connection, transaction))
        {
            user.Parameters.AddWithValue(UserId);
            await using var reader = await user.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken) || reader.GetGuid(0).ToString() != User.FindFirstValue("security_version"))
                return Unauthorized();
            email = reader.GetString(1);
        }
        await using (var existing = new NpgsqlCommand("SELECT id FROM orders WHERE user_id = $1 AND checkout_key = $2", connection, transaction))
        {
            existing.Parameters.AddWithValue(UserId);
            existing.Parameters.AddWithValue(request.CheckoutKey);
            if (await existing.ExecuteScalarAsync(cancellationToken) is Guid previous)
                return Ok(new { id = previous });
        }
        var items = new List<OrderItem>();
        await using (var query = new NpgsqlCommand("""
            SELECT p.id, p.name, p.price, c.quantity, p.stock, p.is_active
            FROM cart_items c JOIN products p ON p.id = c.product_id
            WHERE c.user_id = $1 ORDER BY p.id FOR UPDATE OF p, c
            """, connection, transaction))
        {
            query.Parameters.AddWithValue(UserId);
            await using var reader = await query.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                if (!reader.GetBoolean(5) || reader.GetInt32(4) < reader.GetInt32(3))
                    return Conflict(new { message = "A product is unavailable or has insufficient stock. Reload your cart." });
                items.Add(new(reader.GetGuid(0), reader.GetString(1), reader.GetDecimal(2), reader.GetInt32(3)));
            }
        }
        if (items.Count == 0) return Conflict(new { message = "Your cart is empty. No order was placed." });
        var total = items.Sum(item => item.Price * item.Quantity);
        if (total != request.ExpectedTotal || items.Count != request.Items.Count || items.Any(item =>
            !request.Items.Any(expected => expected.ProductId == item.ProductId && expected.Quantity == item.Quantity && expected.Price == item.Price)))
            return Conflict(new { message = "Your cart or prices changed. Reload your cart and review the new total." });
        if (request.CardNumber == "4000000000000002")
            return BadRequest(new { message = "The dummy payment was declined. No order was placed. Use the successful test card to retry." });

        var id = Guid.NewGuid();
        await using (var order = new NpgsqlCommand("INSERT INTO orders (id, user_id, checkout_key, total, protected_delivery_address) VALUES ($1,$2,$3,$4,$5)", connection, transaction))
        {
            order.Parameters.AddWithValue(id);
            order.Parameters.AddWithValue(UserId);
            order.Parameters.AddWithValue(request.CheckoutKey);
            order.Parameters.AddWithValue(total);
            order.Parameters.AddWithValue(request.DeliveryAddress.Protect(protection));
            await order.ExecuteNonQueryAsync(cancellationToken);
        }
        foreach (var item in items)
        {
            await using var line = new NpgsqlCommand("""
                INSERT INTO order_items (order_id, product_id, name, price, quantity) VALUES ($1,$2,$3,$4,$5)
                """, connection, transaction);
            line.Parameters.AddWithValue(id);
            line.Parameters.AddWithValue(item.ProductId);
            line.Parameters.AddWithValue(item.Name);
            line.Parameters.AddWithValue(item.Price);
            line.Parameters.AddWithValue(item.Quantity);
            await line.ExecuteNonQueryAsync(cancellationToken);
            await using var stock = new NpgsqlCommand("UPDATE products SET stock = stock - $2, version = gen_random_uuid() WHERE id = $1", connection, transaction);
            stock.Parameters.AddWithValue(item.ProductId);
            stock.Parameters.AddWithValue(item.Quantity);
            await stock.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var clear = new NpgsqlCommand("DELETE FROM cart_items WHERE user_id = $1", connection, transaction))
        {
            clear.Parameters.AddWithValue(UserId);
            await clear.ExecuteNonQueryAsync(cancellationToken);
        }
        var culture = CultureInfo.GetCultureInfo("da-DK");
        string Amount(decimal value) => value.ToString("N2", culture) + " DKK";
        var lines = items.Select(item => $"{item.Quantity} x {item.Name.Replace('\r', ' ').Replace('\n', ' ')}: {Amount(item.Price)} each; {Amount(item.Price * item.Quantity)}");
        var confirmation = new AccountEmail(email, $"Game Store demo order confirmation - {id}",
            $"Your demo order {id} is confirmed.\n\nPayment was simulated. No money was charged and no goods will be shipped.\n\n" +
            string.Join("\n", lines) + $"\n\nTotal: {Amount(total)}\n\nYou can view this order in your Game Store order history after signing in.");
        await using (var queue = new NpgsqlCommand("""
            INSERT INTO email_outbox (id, user_id, purpose, order_id, protected_message, expires_at)
            VALUES ($1, $2, 'order', $3, $4, now() + interval '7 days')
            """, connection, transaction))
        {
            queue.Parameters.AddWithValue(Guid.NewGuid());
            queue.Parameters.AddWithValue(UserId);
            queue.Parameters.AddWithValue(id);
            queue.Parameters.AddWithValue(protection.CreateProtector("GameStore.AccountEmail.v1").Protect(JsonSerializer.Serialize(confirmation)));
            await queue.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        HttpContext.Items["AuditTargetId"] = id;
        return CreatedAtAction(nameof(Get), new { id }, new { id });
    }

    private static OrderSummary ReadSummary(NpgsqlDataReader reader) =>
        new(reader.GetGuid(0), reader.GetDateTime(1), reader.GetDecimal(2), reader.GetString(3), reader.GetString(4));
}

public sealed record OrderSummary(Guid Id, DateTime CreatedAt, decimal Total, string Currency, string PaymentStatus);
public sealed record OrderItem(Guid ProductId, string Name, decimal Price, int Quantity);
public sealed record OrderDetail(Guid Id, DateTime CreatedAt, decimal Total, string Currency, string PaymentStatus, List<OrderItem> Items, DeliveryAddress? DeliveryAddress);
public sealed record CheckoutItem(Guid ProductId, [Range(1, 99)] int Quantity,
    [Range(typeof(decimal), "0", "99999999.99", ParseLimitsInInvariantCulture = true)] decimal Price);

public sealed class CheckoutRequest : IValidatableObject
{
    [Required] public required DeliveryAddress DeliveryAddress { get; init; }
    public required Guid CheckoutKey { get; init; }
    public required decimal ExpectedTotal { get; init; }
    [Required, MinLength(1), MaxLength(100)] public required List<CheckoutItem> Items { get; init; }
    [Required, RegularExpression("[0-9]{13,19}")] public required string CardNumber { get; init; }
    [Required, StringLength(100, MinimumLength = 2)] public required string CardholderName { get; init; }
    [Range(1, 12)] public required int ExpiryMonth { get; init; }
    [Range(2000, 9999)] public required int ExpiryYear { get; init; }
    [Required, RegularExpression("[0-9]{3,4}")] public required string SecurityCode { get; init; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (CheckoutKey == Guid.Empty) yield return new("A checkout key is required.", [nameof(CheckoutKey)]);
        if (ExpectedTotal < 0 || decimal.Round(ExpectedTotal, 2) != ExpectedTotal)
            yield return new("Invalid expected total.", [nameof(ExpectedTotal)]);
        if (Items is not null && (Items.Any(item => item is null || item.ProductId == Guid.Empty) ||
            Items.Select(item => item?.ProductId).Distinct().Count() != Items.Count))
            yield return new("Invalid or duplicate cart items.", [nameof(Items)]);
        var now = DateTime.UtcNow;
        if (ExpiryYear < now.Year || (ExpiryYear == now.Year && ExpiryMonth < now.Month) || ExpiryYear > now.Year + 20)
            yield return new("Use a valid, unexpired expiry date.", [nameof(ExpiryYear)]);
        if (string.IsNullOrWhiteSpace(CardholderName) || CardholderName.Any(char.IsControl))
            yield return new("Enter a valid test cardholder name.", [nameof(CardholderName)]);
        if (CardNumber is not null)
        {
            var sum = 0;
            for (var i = CardNumber.Length - 1; i >= 0; i--)
            {
                var digit = CardNumber[i] - '0';
                if ((CardNumber.Length - 1 - i) % 2 == 1) { digit *= 2; if (digit > 9) digit -= 9; }
                sum += digit;
            }
            if (sum % 10 != 0) yield return new("The card number failed its checksum.", [nameof(CardNumber)]);
            else if (CardNumber is not ("4242424242424242" or "4000000000000002"))
                yield return new("Dummy checkout accepts only the displayed test cards. Never enter real card details.", [nameof(CardNumber)]);
        }
    }
}
