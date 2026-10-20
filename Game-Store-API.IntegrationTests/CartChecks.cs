using System.Net;
using System.Net.Http.Json;
using Game_Store_API.Controllers;

static class CartChecks
{
    public static async Task RunAsync(HttpClient client, string customerToken, string adminToken)
    {
        var original = client.DefaultRequestHeaders.Authorization;
        static void Check(bool ok, string name) { if (!ok) throw new Exception("Cart: " + name); }
        void As(string? token) => client.DefaultRequestHeaders.Authorization = token is null ? null : new("Bearer", token);
        async Task<HttpResponseMessage> Set(Guid id, int quantity) => await client.PutAsJsonAsync($"/api/cart/items/{id}", new { quantity });
        try
        {
            As(null);
            Check((await client.GetAsync("/api/cart")).StatusCode == HttpStatusCode.Unauthorized, "anonymous read denied");
            Check((await Set(Guid.NewGuid(), 1)).StatusCode == HttpStatusCode.Unauthorized, "anonymous write denied");
            As(adminToken);
            using var created = await client.PostAsJsonAsync("/api/products", new { name = "Cart test product", price = 12.50m, stock = 5 });
            Check(created.IsSuccessStatusCode, "create test product");
            var product = (await created.Content.ReadFromJsonAsync<Product>())!;
            As(customerToken);
            Check((await client.GetFromJsonAsync<Cart>("/api/cart"))!.Items.Count == 0, "initial empty cart");
            foreach (var quantity in new[] { 0, -1, 100 }) Check((await Set(product.Id, quantity)).StatusCode == HttpStatusCode.BadRequest, "quantity validation");
            Check((await client.PutAsJsonAsync($"/api/cart/items/{product.Id}", new { })).StatusCode == HttpStatusCode.BadRequest, "missing quantity rejected");
            Check((await Set(Guid.NewGuid(), 1)).StatusCode == HttpStatusCode.NotFound, "missing product");
            Check((await Set(product.Id, 6)).StatusCode == HttpStatusCode.Conflict, "stock enforcement");
            Check((await client.PutAsJsonAsync($"/api/cart/items/{product.Id}", new { quantity = 2, price = 0, userId = "ignored" })).StatusCode == HttpStatusCode.NoContent, "customer adds product");
            var cart = (await client.GetFromJsonAsync<Cart>("/api/cart"))!;
            Check(cart.Items.Count == 1 && cart.Total == 25m && cart.TotalQuantity == 2 && cart.Items[0].Price == 12.50m, "server totals and price tampering rejection");
            Check((await Set(product.Id, 3)).IsSuccessStatusCode, "set quantity");
            Check((await client.GetFromJsonAsync<Cart>("/api/cart"))!.Items.Count == 1, "quantity updates do not duplicate lines");
            As(adminToken);
            Check((await client.GetFromJsonAsync<Cart>("/api/cart"))!.Items.Count == 0, "account isolation");
            Check((await client.DeleteAsync($"/api/cart/items/{product.Id}")).StatusCode == HttpStatusCode.NotFound, "cannot remove another account item");
            using var update = new HttpRequestMessage(HttpMethod.Put, $"/api/products/{product.Id}") { Content = JsonContent.Create(new { name = product.Name, price = 20m, stock = 1, isActive = false }) };
            update.Headers.IfMatch.ParseAdd($"\"{product.Version}\"");
            Check((await client.SendAsync(update)).IsSuccessStatusCode, "catalog changes");
            As(customerToken);
            cart = (await client.GetFromJsonAsync<Cart>("/api/cart"))!;
            Check(cart.Total == 60m && cart.Items[0].Stock == 1 && !cart.Items[0].IsActive, "cart reflects current price and availability");
            Check((await Set(product.Id, 1)).StatusCode == HttpStatusCode.Conflict, "inactive item cannot be added");
            Check((await client.DeleteAsync($"/api/cart/items/{product.Id}")).StatusCode == HttpStatusCode.NoContent, "unavailable item removable");
            Check((await client.DeleteAsync("/api/cart")).StatusCode == HttpStatusCode.NoContent, "clear cart idempotent");
            Check((await client.GetFromJsonAsync<Cart>("/api/cart"))!.Total == 0m, "empty total");
        }
        finally { client.DefaultRequestHeaders.Authorization = original; }
        Console.WriteLine("PASS: cart authentication, account isolation, quantities, stock checks, current prices, trusted totals, removal and clearing.");
    }
}
