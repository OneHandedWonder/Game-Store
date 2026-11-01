using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Game_Store_API.Controllers;
using Npgsql;

static class OrderChecks
{
    public static async Task RunAsync(HttpClient client, NpgsqlDataSource source, string customerToken, string adminToken, string employeeToken, string logDirectory)
    {
        var original = client.DefaultRequestHeaders.Authorization;
        static void Check(bool ok, string name) { if (!ok) throw new Exception("Orders: " + name); }
        void As(string? token) => client.DefaultRequestHeaders.Authorization = token is null ? null : new("Bearer", token);
        async Task<HttpResponseMessage> Pay(CheckoutRequest request, string? token = null)
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, "/api/orders/checkout") { Content = JsonContent.Create(request) };
            message.Headers.Authorization = new("Bearer", token ?? customerToken);
            return await client.SendAsync(message);
        }
        CheckoutRequest Request(Guid id, Guid key, string number = "4242424242424242", decimal price = 12.50m,
            decimal total = 25m, int year = 0, string code = "123", int quantity = 2) => new()
        {
            DeliveryAddress = new Game_Store_API.Data.DeliveryAddress("Demo Recipient", "Example Street 42", "Suite 2", "1234", "Demo City", "DK"),
            CheckoutKey = key, ExpectedTotal = total, Items = [new(id, quantity, price)],
            CardNumber = number, CardholderName = "Demo Customer", ExpiryMonth = 12,
            ExpiryYear = year == 0 ? DateTime.UtcNow.Year + 1 : year, SecurityCode = code,
        };
        async Task<Product> Create(string name, int stock)
        {
            As(adminToken);
            using var result = await client.PostAsJsonAsync("/api/products", new { name, price = 12.50m, stock });
            Check(result.IsSuccessStatusCode, "create product");
            return (await result.Content.ReadFromJsonAsync<Product>())!;
        }
        async Task<Guid> OrderId(HttpResponseMessage response)
        {
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return body.RootElement.GetProperty("id").GetGuid();
        }
        try
        {
            As(null);
            Check((await client.GetAsync("/api/staff/orders")).StatusCode == HttpStatusCode.Unauthorized, "anonymous staff list denied");
            Check((await client.GetAsync("/api/staff/orders/metrics")).StatusCode == HttpStatusCode.Unauthorized, "anonymous metrics denied");
            Check((await client.GetAsync("/api/staff/orders/analytics")).StatusCode == HttpStatusCode.Unauthorized, "anonymous analytics denied");
            Check((await client.GetAsync($"/api/staff/orders/{Guid.NewGuid()}")).StatusCode == HttpStatusCode.Unauthorized, "anonymous staff details denied");
            Check((await client.GetAsync("/api/orders")).StatusCode == HttpStatusCode.Unauthorized, "anonymous history denied");
            Check((await client.PostAsJsonAsync("/api/orders/checkout", Request(Guid.NewGuid(), Guid.NewGuid()))).StatusCode == HttpStatusCode.Unauthorized, "anonymous checkout denied");
            var product = await Create("Order snapshot game", 5);
            As(customerToken);
            Check((await client.PutAsJsonAsync($"/api/cart/items/{product.Id}", new { quantity = 2 })).IsSuccessStatusCode, "fill cart");
            foreach (var request in new[] {
                Request(product.Id, Guid.NewGuid(), number: "4242424242424241"),
                Request(product.Id, Guid.NewGuid(), number: "4012888888881881"),
                Request(product.Id, Guid.NewGuid(), year: 2000),
                Request(product.Id, Guid.NewGuid(), code: "ab1"),
                Request(product.Id, Guid.Empty),
                Request(product.Id, Guid.NewGuid(), number: "4000000000000002"),
            })
            {
                using var response = await Pay(request);
                Check(response.StatusCode == HttpStatusCode.BadRequest, $"invalid/test-declined card rejected ({response.StatusCode}: {await response.Content.ReadAsStringAsync()})");
            }
            foreach (var request in new[] { Request(product.Id, Guid.NewGuid(), price: 0, total: 0), Request(product.Id, Guid.NewGuid(), quantity: 1) })
                Check((await Pay(request)).StatusCode == HttpStatusCode.Conflict, "price/quantity tampering rejected");
            Check((await client.GetFromJsonAsync<Cart>("/api/cart"))!.Total == 25m, "failed payment preserves cart");
            var valid = Request(product.Id, Guid.NewGuid());
            foreach (var address in new object?[] { null, new { recipient = "Demo", line1 = "Invalid\nStreet", postalCode = "1234", city = "City", countryCode = "DK" } })
            {
                var body = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(valid))!;
                body["DeliveryAddress"] = JsonSerializer.SerializeToNode(address);
                Check((await client.PostAsJsonAsync("/api/orders/checkout", body)).StatusCode == HttpStatusCode.BadRequest, "missing/unsafe address rejected");
            }
            var repeated = await Task.WhenAll(Pay(valid), Pay(valid));
            Check(repeated.Count(r => r.StatusCode == HttpStatusCode.Created) == 1 && repeated.Count(r => r.StatusCode == HttpStatusCode.OK) == 1, "concurrent retry creates only one order");
            var id = await OrderId(repeated[0]);
            Check(id == await OrderId(repeated[1]), "same key returns same order");
            Check((await client.GetFromJsonAsync<Cart>("/api/cart"))!.Items.Count == 0, "successful checkout clears cart");
            var order = (await client.GetFromJsonAsync<OrderDetail>($"/api/orders/{id}"))!;
            Check(order.DeliveryAddress?.Line1 == "Example Street 42" && order.DeliveryAddress.CountryCode == "DK", "customer address round trip");
            await using (var stored = source.CreateCommand("SELECT protected_delivery_address FROM orders WHERE id=$1"))
            {
                stored.Parameters.AddWithValue(id);
                var payload = (string)(await stored.ExecuteScalarAsync())!;
                Check(!payload.Contains("Example Street 42") && !payload.Contains("Demo Recipient"), "address encrypted at rest");
            }
            Check(!(await client.GetStringAsync("/api/orders")).Contains("Example Street 42"), "address excluded from lists");
            Check((await client.GetAsync("/api/staff/orders")).StatusCode == HttpStatusCode.Forbidden, "customer staff list denied");
            Check((await client.GetAsync("/api/staff/orders/metrics")).StatusCode == HttpStatusCode.Forbidden, "customer metrics denied");
            Check((await client.GetAsync("/api/staff/orders/analytics")).StatusCode == HttpStatusCode.Forbidden, "customer analytics denied");
            Check((await client.PutAsJsonAsync($"/api/staff/orders/{id}/fulfillment", new { status = "fulfilled", version = Guid.NewGuid() })).StatusCode == HttpStatusCode.Forbidden, "customer fulfillment denied");
            Check((await client.GetAsync($"/api/staff/orders/{id}")).StatusCode == HttpStatusCode.Forbidden, "customer staff details denied");
            foreach (var staffToken in new[] { employeeToken, adminToken })
            {
                As(staffToken);
                var staffOrders = (await client.GetFromJsonAsync<List<StaffOrderSummary>>("/api/staff/orders"))!;
                Check(staffOrders.Single().Id == id && staffOrders.Single().Total == 25m, "staff can list customer orders");
                var staffOrder = (await client.GetFromJsonAsync<StaffOrderDetail>($"/api/staff/orders/{id}"))!;
                Check(staffOrder.Items.Single().Name == product.Name && staffOrder.CustomerEmail == "ACCOUNT@EXAMPLE.COM", "staff can read saved items and customer");
                Check(staffOrder.DeliveryAddress?.Recipient == "Demo Recipient", "staff reads address snapshot");
                var analytics = (await client.GetFromJsonAsync<OrderAnalytics>("/api/staff/orders/analytics?days=7"))!;
                Check(analytics.Daily.Count == 7 && analytics.Daily.Sum(day => day.Orders) == 1 && analytics.Daily.Sum(day => day.Sales) == 25m && analytics.Daily.Count(day => day.Orders == 0) == 6, "dated chart series includes zero days and server totals");
                Check(analytics.TopProducts.Single().Units == 2 && analytics.TopProducts.Single().Sales == 25m, "top products use order snapshots");
                Check((await client.GetAsync("/api/staff/orders/analytics?days=91")).StatusCode == HttpStatusCode.BadRequest, "analytics period bounded");
                var metrics = (await client.GetFromJsonAsync<ManagementMetrics>("/api/staff/orders/metrics"))!;
                Check(metrics.TotalOrders == 1 && metrics.UnfulfilledOrders == 1 && metrics.FulfilledOrders == 0 && metrics.TotalSales == 25m && metrics.AverageOrderValue == 25m && metrics.UnitsOrdered == 2 && metrics.OrderingCustomers == 1 && metrics.OrdersToday == 1, "real aggregate metrics");
                Check(staffOrder.FulfillmentStatus == "unfulfilled", "new orders start unfulfilled");
                Check((await client.PutAsJsonAsync($"/api/staff/orders/{id}/fulfillment", new { status = "invalid", version = staffOrder.FulfillmentVersion })).StatusCode == HttpStatusCode.BadRequest, "invalid status rejected");
                Check((await client.PutAsJsonAsync($"/api/staff/orders/{id}/fulfillment", new { status = "fulfilled", version = staffOrder.FulfillmentVersion })).IsSuccessStatusCode, "staff fulfills order");
                Check((await client.PutAsJsonAsync($"/api/staff/orders/{id}/fulfillment", new { status = "unfulfilled", version = staffOrder.FulfillmentVersion })).StatusCode == HttpStatusCode.Conflict, "stale fulfillment update rejected");
                metrics = (await client.GetFromJsonAsync<ManagementMetrics>("/api/staff/orders/metrics"))!;
                Check(metrics.FulfilledOrders == 1 && metrics.UnfulfilledOrders == 0 && metrics.TotalSales == 25m, "metrics reflect fulfillment without changing sales");
                var fulfilled = (await client.GetFromJsonAsync<StaffOrderDetail>($"/api/staff/orders/{id}"))!;
                Check((await client.PutAsJsonAsync($"/api/staff/orders/{id}/fulfillment", new { status = "unfulfilled", version = fulfilled.FulfillmentVersion })).IsSuccessStatusCode, "staff reopens order");
                using var staffResponse = await client.GetAsync($"/api/staff/orders/{id}");
                Check(staffResponse.Headers.CacheControl?.NoStore == true, "staff details not cached");
                Check((await client.GetFromJsonAsync<List<StaffOrderSummary>>("/api/staff/orders?page=2"))!.Count == 0, "staff pagination");
                Check((await client.GetAsync("/api/staff/orders?page=0")).StatusCode == HttpStatusCode.BadRequest, "invalid staff page rejected");
                Check((await client.GetAsync($"/api/staff/orders/{Guid.NewGuid()}")).StatusCode == HttpStatusCode.NotFound, "missing staff order");
            }
            As(customerToken);
            Check(order.Total == 25m && order.Currency == "DKK" && order.PaymentStatus == "simulated" && order.Items.Single().Quantity == 2, "server order snapshot");
            Check(client.DefaultRequestHeaders.Authorization is not null, "authenticated history");
            using (var response = await client.GetAsync($"/api/orders/{id}")) Check(response.Headers.CacheControl?.NoStore == true, "private order not cached");
            As(adminToken);
            Check((await client.GetAsync($"/api/orders/{id}")).StatusCode == HttpStatusCode.NotFound, "admin cannot read another customer's order");
            Check((await client.GetFromJsonAsync<List<OrderSummary>>("/api/orders"))!.Count == 0, "history account isolation");
            var after = (await client.GetFromJsonAsync<Product>($"/api/products/{product.Id}"))!;
            Check(after.Stock == 3 && after.Version != product.Version, "stock deducted once and version rotated");
            using var delete = new HttpRequestMessage(HttpMethod.Delete, $"/api/products/{product.Id}");
            delete.Headers.IfMatch.ParseAdd($"\"{after.Version}\"");
            Check((await client.SendAsync(delete)).IsSuccessStatusCode, "delete purchased product");
            As(customerToken);
            Check((await client.GetFromJsonAsync<OrderDetail>($"/api/orders/{id}"))!.Items.Single().Name == product.Name, "history survives product deletion");
            Check((await Pay(valid)).StatusCode == HttpStatusCode.OK, "retry survives empty cart and product deletion");
            Check((await Pay(Request(product.Id, Guid.NewGuid()))).StatusCode == HttpStatusCode.Conflict, "empty cart cannot create new order");

            var scarce = await Create("Last copy game", 1);
            As(customerToken);
            Check((await client.PutAsJsonAsync($"/api/cart/items/{scarce.Id}", new { quantity = 1 })).IsSuccessStatusCode, "customer competing cart");
            As(adminToken);
            Check((await client.PutAsJsonAsync($"/api/cart/items/{scarce.Id}", new { quantity = 1 })).IsSuccessStatusCode, "admin competing cart");
            var competing = await Task.WhenAll(
                Pay(Request(scarce.Id, Guid.NewGuid(), total: 12.50m, quantity: 1)),
                Pay(Request(scarce.Id, Guid.NewGuid(), total: 12.50m, quantity: 1), adminToken));
            Check(competing.Count(r => r.StatusCode == HttpStatusCode.Created) == 1 && competing.Count(r => r.StatusCode == HttpStatusCode.Conflict) == 1, "concurrent buyers cannot oversell");
            Check((await client.GetFromJsonAsync<Product>($"/api/products/{scarce.Id}"))!.Stock == 0, "stock never negative");

            await using var columns = source.CreateCommand("SELECT column_name FROM information_schema.columns WHERE table_name IN ('orders','order_items')");
            await using var reader = await columns.ExecuteReaderAsync();
            while (await reader.ReadAsync()) Check(!reader.GetString(0).Contains("card") && !reader.GetString(0).Contains("security_code"), "no card storage columns");
            var logs = "";
            foreach (var path in Directory.GetFiles(logDirectory, "application-*.jsonl"))
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var logReader = new StreamReader(stream);
                logs += await logReader.ReadToEndAsync();
            }
            Check(!logs.Contains("4242424242424242") && !logs.Contains("4000000000000002") && !logs.Contains("Demo Customer"), "card details excluded from logs");
            Check(!logs.Contains("Example Street 42") && !logs.Contains("Demo Recipient"), "address excluded from logs");
            var rateLimited = false;
            for (var i = 0; i < 21 && !rateLimited; i++)
            {
                using var response = await Pay(Request(scarce.Id, Guid.NewGuid(), total: 12.50m, quantity: 1));
                rateLimited = response.StatusCode == HttpStatusCode.TooManyRequests;
            }
            Check(rateLimited, "checkout rate limit");
        }
        finally { client.DefaultRequestHeaders.Authorization = original; }
        Console.WriteLine("PASS: dummy payment validation/decline, trusted totals, atomic orders, idempotent retries, stock concurrency, private history, snapshots and payment log exclusion.");
    }
}
