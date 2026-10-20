using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Npgsql;

namespace Game_Store_API.Controllers;

[ApiController]
[Authorize]
[Route("api/cart")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[RequestSizeLimit(4096)]
public sealed class CartController(NpgsqlDataSource dataSource) : ControllerBase
{
    private Guid UserId => Guid.Parse(User.FindFirstValue("sub")!);

    [HttpGet]
    public async Task<ActionResult<Cart>> Get(CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("""
            SELECT p.id, p.name, p.price, p.stock, p.image_url, p.is_active, c.quantity
            FROM cart_items c JOIN products p ON p.id = c.product_id
            WHERE c.user_id = $1 ORDER BY p.name, p.id
            """);
        command.Parameters.AddWithValue(UserId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var items = new List<CartItem>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var price = reader.GetDecimal(2);
            var quantity = reader.GetInt32(6);
            items.Add(new CartItem(reader.GetGuid(0), reader.GetString(1), price, reader.GetInt32(3),
                reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetBoolean(5), quantity, price * quantity));
        }
        return Ok(new Cart(items, items.Sum(item => item.LineTotal), items.Sum(item => item.Quantity)));
    }

    [HttpPut("items/{productId:guid}")]
    public async Task<IActionResult> SetQuantity(Guid productId, CartQuantityRequest request, CancellationToken cancellationToken)
    {
        var quantity = request.Quantity!.Value;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var product = new NpgsqlCommand("SELECT stock, is_active FROM products WHERE id = $1 FOR SHARE", connection, transaction);
        product.Parameters.AddWithValue(productId);
        await using (var reader = await product.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken)) return NotFound();
            if (!reader.GetBoolean(1) || reader.GetInt32(0) < quantity)
                return Conflict(new { message = "This product is unavailable or has insufficient stock. Reload your cart." });
        }
        await using var update = new NpgsqlCommand("""
            INSERT INTO cart_items (user_id, product_id, quantity) VALUES ($1, $2, $3)
            ON CONFLICT (user_id, product_id) DO UPDATE SET quantity = EXCLUDED.quantity
            """, connection, transaction);
        update.Parameters.AddWithValue(UserId);
        update.Parameters.AddWithValue(productId);
        update.Parameters.AddWithValue(quantity);
        await update.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return NoContent();
    }

    [HttpDelete("items/{productId:guid}")]
    public async Task<IActionResult> Remove(Guid productId, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("DELETE FROM cart_items WHERE user_id = $1 AND product_id = $2");
        command.Parameters.AddWithValue(UserId);
        command.Parameters.AddWithValue(productId);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1 ? NoContent() : NotFound();
    }

    [HttpDelete]
    public async Task<IActionResult> Clear(CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("DELETE FROM cart_items WHERE user_id = $1");
        command.Parameters.AddWithValue(UserId);
        await command.ExecuteNonQueryAsync(cancellationToken);
        return NoContent();
    }
}

public sealed record CartQuantityRequest([Required, Range(1, 99)] int? Quantity);
public sealed record CartItem(Guid ProductId, string Name, decimal Price, int Stock,
    string? ImageUrl, bool IsActive, int Quantity, decimal LineTotal);
public sealed record Cart(List<CartItem> Items, decimal Total, int TotalQuantity);
