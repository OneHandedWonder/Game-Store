using System.ComponentModel.DataAnnotations;
using Game_Store_API.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Npgsql;

namespace Game_Store_API.Controllers;

[ApiController]
[Route("api/products")]
[RequestSizeLimit(32_768)]
public sealed class ProductsController(NpgsqlDataSource dataSource) : ControllerBase
{
    [AllowAnonymous]
    [HttpGet]
    public async Task<ActionResult<List<Product>>> List(
        [Range(1, 1_000_000)] int page = 1, [Range(1, 100)] int pageSize = 50,
        bool includeInactive = false, [MaxLength(200)] string? search = null,
        [RegularExpression("name|low|high")] string sort = "name", CancellationToken cancellationToken = default)
    {
        if (includeInactive && !IsStaff())
            return User.Identity?.IsAuthenticated == true ? Forbid() : Challenge();
        var order = sort switch { "low" => "price, name, id", "high" => "price DESC, name, id", _ => "name, id" };
        await using var command = dataSource.CreateCommand($"""
            SELECT id, name, description, price, stock, image_url, is_active, version FROM products
            WHERE (is_active OR $1) AND ($4 = '' OR strpos(lower(name || ' ' || description), lower($4)) > 0)
            ORDER BY {order} LIMIT $2 OFFSET $3
            """);
        command.Parameters.AddWithValue(includeInactive);
        command.Parameters.AddWithValue(pageSize);
        command.Parameters.AddWithValue((page - 1) * pageSize);
        command.Parameters.AddWithValue(search?.Trim() ?? "");
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var products = new List<Product>();
        while (await reader.ReadAsync(cancellationToken)) products.Add(Read(reader));
        return Ok(products);
    }

    [AllowAnonymous]
    [HttpGet("{id:guid}")]
    public async Task<ActionResult<Product>> Get(Guid id, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("""
            SELECT id, name, description, price, stock, image_url, is_active, version FROM products
            WHERE id = $1 AND (is_active OR $2)
            """);
        command.Parameters.AddWithValue(id);
        command.Parameters.AddWithValue(IsStaff());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return NotFound();
        var product = Read(reader);
        Response.Headers.ETag = $"\"{product.Version}\"";
        return Ok(product);
    }

    [Authorize(Roles = AppRoles.Staff)]
    [HttpPost]
    public async Task<ActionResult<Product>> Create(ProductRequest request, CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid();
        await using var command = dataSource.CreateCommand("""
            INSERT INTO products (id, name, description, price, stock, image_url, is_active)
            VALUES ($1, $2, $3, $4, $5, $6, $7)
            RETURNING id, name, description, price, stock, image_url, is_active, version
            """);
        AddParameters(command, id, request);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) throw new InvalidOperationException("Product insertion returned no row.");
        var product = Read(reader);
        Response.Headers.ETag = $"\"{product.Version}\"";
        return CreatedAtAction(nameof(Get), new { id }, product);
    }

    [Authorize(Roles = AppRoles.Staff)]
    [HttpPut("{id:guid}")]
    public async Task<ActionResult<Product>> Update(Guid id, ProductRequest request,
        [FromHeader(Name = "If-Match")] string? ifMatch, CancellationToken cancellationToken)
    {
        if (!TryVersion(ifMatch, out var version)) return VersionRequired(ifMatch);
        await using var command = dataSource.CreateCommand("""
            UPDATE products SET name = $2, description = $3, price = $4,
                stock = $5, image_url = $6, is_active = $7, version = gen_random_uuid()
            WHERE id = $1 AND version = $8
            RETURNING id, name, description, price, stock, image_url, is_active, version
            """);
        AddParameters(command, id, request);
        command.Parameters.AddWithValue(version);
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (await reader.ReadAsync(cancellationToken))
            {
                var product = Read(reader);
                Response.Headers.ETag = $"\"{product.Version}\"";
                return Ok(product);
            }
        }
        return await MissingOrChanged(id, cancellationToken);
    }

    [Authorize(Roles = AppRoles.Staff)]
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, [FromHeader(Name = "If-Match")] string? ifMatch,
        CancellationToken cancellationToken)
    {
        if (!TryVersion(ifMatch, out var version)) return VersionRequired(ifMatch);
        await using var command = dataSource.CreateCommand("DELETE FROM products WHERE id = $1 AND version = $2");
        command.Parameters.AddWithValue(id);
        command.Parameters.AddWithValue(version);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1 ? NoContent() : await MissingOrChanged(id, cancellationToken);
    }

    private static bool TryVersion(string? value, out Guid version)
    {
        version = default;
        return value is { Length: 38 } && value[0] == '"' && value[^1] == '"'
            && Guid.TryParseExact(value[1..^1], "D", out version);
    }

    private ObjectResult VersionRequired(string? value) => StatusCode(value is null ? 428 : 400,
        new { message = "Supply If-Match with the quoted version from the product you loaded." });

    private async Task<ActionResult> MissingOrChanged(Guid id, CancellationToken cancellationToken)
    {
        await using var exists = dataSource.CreateCommand("SELECT EXISTS (SELECT 1 FROM products WHERE id = $1)");
        exists.Parameters.AddWithValue(id);
        return await exists.ExecuteScalarAsync(cancellationToken) is true
            ? StatusCode(412, new { message = "This product changed since you opened it. Reload it before saving or deleting." }) : NotFound();
    }

    private bool IsStaff() => User.IsInRole(AppRoles.Employee) || User.IsInRole(AppRoles.Admin);

    private static Product Read(NpgsqlDataReader reader) => new(reader.GetGuid(0), reader.GetString(1),
        reader.GetString(2), reader.GetDecimal(3), reader.GetInt32(4),
        reader.IsDBNull(5) ? null : reader.GetString(5), reader.GetBoolean(6), reader.GetGuid(7));

    private static void AddParameters(NpgsqlCommand command, Guid id, ProductRequest request)
    {
        command.Parameters.AddWithValue(id);
        command.Parameters.AddWithValue(request.Name.Trim());
        command.Parameters.AddWithValue(request.Description);
        command.Parameters.AddWithValue(request.Price);
        command.Parameters.AddWithValue(request.Stock);
        command.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Text, (object?)request.ImageUrl ?? DBNull.Value);
        command.Parameters.AddWithValue(request.IsActive);
    }
}

public sealed record Product(Guid Id, string Name, string Description, decimal Price,
    int Stock, string? ImageUrl, bool IsActive, Guid Version);

public sealed class ProductRequest : IValidatableObject
{
    [Required, MaxLength(200)]
    public required string Name { get; init; }

    [Required(AllowEmptyStrings = true), MaxLength(4000)]
    public string Description { get; init; } = "";

    public required decimal Price { get; init; }

    [Range(0, int.MaxValue)]
    public required int Stock { get; init; }

    [MaxLength(2048)]
    public string? ImageUrl { get; init; }

    public bool IsActive { get; init; } = true;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Price < 0 || Price > 99999999.99m)
            yield return new ValidationResult("Price must be between 0 and 99999999.99 DKK.", [nameof(Price)]);
        if (decimal.Round(Price, 2) != Price)
            yield return new ValidationResult("Price must have at most two decimal places.", [nameof(Price)]);
        if (ImageUrl is not null && (!Uri.TryCreate(ImageUrl, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)))
            yield return new ValidationResult("ImageUrl must be an absolute HTTP or HTTPS URL.", [nameof(ImageUrl)]);
    }
}
