using System.Net;
using System.Net.Http.Json;
using Game_Store_API.Controllers;

static class CatalogChecks
{
    public static async Task RunAsync(HttpClient client, string defaultToken, string employeeToken, string adminToken)
    {
        var original = client.DefaultRequestHeaders.Authorization;
        var request = new { name = "Game ' test", description = "Product details", price = 249.95m, stock = 5, imageUrl = (string?)null, isActive = true };
        void Authenticate(string? token) => client.DefaultRequestHeaders.Authorization = token is null ? null : new("Bearer", token);
        static void Check(bool result, string message) { if (!result) throw new Exception("Catalog: " + message); }
        try
        {
            Authenticate(null);
            Check((await client.GetAsync("/api/products")).IsSuccessStatusCode, "public list");
            Check((await client.PostAsJsonAsync("/api/products", request)).StatusCode == HttpStatusCode.Unauthorized, "anonymous creation denied");
            Check((await client.GetAsync("/api/products?includeInactive=true")).StatusCode == HttpStatusCode.Unauthorized, "anonymous inactive list denied");
            Authenticate(defaultToken);
            Check((await client.PostAsJsonAsync("/api/products", request)).StatusCode == HttpStatusCode.Forbidden, "customer creation denied");
            Check((await client.GetAsync("/api/products?includeInactive=true")).StatusCode == HttpStatusCode.Forbidden, "customer inactive list denied");
            Authenticate(employeeToken);
            foreach (var invalid in new object[]
            {
                new { name = "Bad", price = -1, stock = 1 }, new { name = "Bad", price = 1, stock = -1 },
                new { name = "Bad", price = 1.123m, stock = 1 }, new { name = "   ", price = 1, stock = 1 },
                new { name = "Bad", price = 1, stock = 1, imageUrl = "javascript:alert(1)" },
                new { name = "Missing price", stock = 1 }, new { name = "Missing stock", price = 1 }
            })
            {
                using var response = await client.PostAsJsonAsync("/api/products", invalid);
                Check(response.StatusCode == HttpStatusCode.BadRequest,
                    $"invalid product rejected: {System.Text.Json.JsonSerializer.Serialize(invalid)} returned {(int)response.StatusCode}");
            }
            using var created = await client.PostAsJsonAsync("/api/products", request);
            Check(created.StatusCode == HttpStatusCode.Created && created.Headers.Location is not null, "employee creation and location");
            var product = (await created.Content.ReadFromJsonAsync<Product>())!;
            Check(product.Price == 249.95m && product.Stock == 5 && product.Name == request.name, "product stored accurately");
            Authenticate(null);
            Check((await client.GetAsync($"/api/products/{product.Id}")).IsSuccessStatusCode, "active detail public");
            Check((await client.GetAsync("/api/products?page=0")).StatusCode == HttpStatusCode.BadRequest, "invalid page");
            Check((await client.GetAsync("/api/products?pageSize=101")).StatusCode == HttpStatusCode.BadRequest, "bounded list");
            Check((await client.PutAsJsonAsync($"/api/products/{product.Id}", request)).StatusCode == HttpStatusCode.Unauthorized, "anonymous update denied");
            Authenticate(defaultToken);
            Check((await client.DeleteAsync($"/api/products/{product.Id}")).StatusCode == HttpStatusCode.Forbidden, "customer delete denied");
            Authenticate(adminToken);
            Check((await client.PutAsJsonAsync($"/api/products/{product.Id}", request)).StatusCode == (HttpStatusCode)428, "update requires version");
            client.DefaultRequestHeaders.IfMatch.ParseAdd($"\"{product.Version}\"");
            using var updated = await client.PutAsJsonAsync($"/api/products/{product.Id}", new { request.name, price = 0m, stock = 0, isActive = false });
            Check(updated.IsSuccessStatusCode, "admin updates product with zero price and stock");
            var current = (await updated.Content.ReadFromJsonAsync<Product>())!;
            Check(current.Version != product.Version, "update rotates version");
            Check((await client.PutAsJsonAsync($"/api/products/{product.Id}", request)).StatusCode == HttpStatusCode.PreconditionFailed, "stale update rejected");
            Check((await client.DeleteAsync($"/api/products/{product.Id}")).StatusCode == HttpStatusCode.PreconditionFailed, "stale delete rejected");
            Authenticate(null);
            Check((await client.GetAsync($"/api/products/{product.Id}")).StatusCode == HttpStatusCode.NotFound, "inactive detail hidden");
            Check((await client.GetFromJsonAsync<Product[]>("/api/products"))!.Length == 0, "inactive list hidden");
            Authenticate(employeeToken);
            Check((await client.GetAsync($"/api/products/{product.Id}")).IsSuccessStatusCode, "staff can read inactive detail");
            Check((await client.GetFromJsonAsync<Product[]>("/api/products?includeInactive=true"))!.Length == 1, "staff can list inactive products");
            client.DefaultRequestHeaders.IfMatch.Clear();
            client.DefaultRequestHeaders.IfMatch.ParseAdd($"\"{current.Version}\"");
            var concurrent = await Task.WhenAll(
                client.PutAsJsonAsync($"/api/products/{product.Id}", new { name = "First edit", price = 1, stock = 7, isActive = false }),
                client.PutAsJsonAsync($"/api/products/{product.Id}", new { name = "Second edit", price = 2, stock = 8, isActive = false }));
            Check(concurrent.Count(response => response.StatusCode == HttpStatusCode.OK) == 1
                && concurrent.Count(response => response.StatusCode == HttpStatusCode.PreconditionFailed) == 1, "concurrent writes cannot overwrite each other");
            var winner = (await concurrent.Single(response => response.IsSuccessStatusCode).Content.ReadFromJsonAsync<Product>())!;
            foreach (var response in concurrent) response.Dispose();
            client.DefaultRequestHeaders.IfMatch.Clear();
            client.DefaultRequestHeaders.IfMatch.ParseAdd($"\"{winner.Version}\"");
            Check((await client.PutAsJsonAsync($"/api/products/{Guid.NewGuid()}", request)).StatusCode == HttpStatusCode.NotFound, "missing update");
            Check((await client.DeleteAsync($"/api/products/{product.Id}")).StatusCode == HttpStatusCode.NoContent, "employee delete");
            Check((await client.DeleteAsync($"/api/products/{product.Id}")).StatusCode == HttpStatusCode.NotFound, "missing delete");
            client.DefaultRequestHeaders.IfMatch.Clear();
            for (var i = 0; i < 14; i++)
            {
                using var seeded = await client.PostAsJsonAsync("/api/products", new { name = $"Paged game {i:D2}", price = 14 - i, stock = 1 });
                Check(seeded.StatusCode == HttpStatusCode.Created, "pagination fixture");
            }
            var searched = await client.GetFromJsonAsync<Product[]>("/api/products?search=game%2013&pageSize=1");
            Check(searched!.Length == 1 && searched[0].Name == "Paged game 13", "search before pagination");
            var cheapest = await client.GetFromJsonAsync<Product[]>("/api/products?sort=low&pageSize=1");
            var mostExpensive = await client.GetFromJsonAsync<Product[]>("/api/products?sort=high&pageSize=1");
            Check(cheapest![0].Price == 1 && mostExpensive![0].Price == 14, "price order before pagination");
            Check((await client.GetAsync("/api/products?sort=bad")).StatusCode == HttpStatusCode.BadRequest, "unknown sort rejected");
            Check((await client.GetFromJsonAsync<Product[]>("/api/products?search=%25"))!.Length == 0, "literal search does not treat wildcard as all products");
        }
        finally { client.DefaultRequestHeaders.Authorization = original; client.DefaultRequestHeaders.IfMatch.Clear(); }
        Console.WriteLine("PASS: catalog CRUD, decimal prices, input validation, inactive visibility, pagination and role permissions.");
    }
}
