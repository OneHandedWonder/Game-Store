using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;

static class LoggingChecks
{
    public static async Task RunAsync(HttpClient client, NpgsqlDataSource source, string customer, string employee,
        string admin, string directory)
    {
        var original = client.DefaultRequestHeaders.Authorization;
        void As(string? token) => client.DefaultRequestHeaders.Authorization = token is null ? null : new("Bearer", token);
        static void Check(bool ok, string name) { if (!ok) throw new Exception("Logging: " + name); }
        async Task Execute(string sql) { await using var command = source.CreateCommand(sql); await command.ExecuteNonQueryAsync(); }
        const string sentinel = "DO_NOT_LOG_SECRET_7cfe!";
        try
        {
            As(null);
            Check((await client.GetAsync("/api/monitoring/events")).StatusCode == HttpStatusCode.Unauthorized, "anonymous logs denied");
            As(customer);
            Check((await client.GetAsync("/api/monitoring/events")).StatusCode == HttpStatusCode.Forbidden, "customer logs denied");
            As(employee);
            Check((await client.GetAsync("/api/monitoring/summary")).StatusCode == HttpStatusCode.Forbidden, "employee monitoring denied");
            As(null);
            using var failedLogin = await client.PostAsJsonAsync("/api/auth/login", new { email = "audit-test@example.com", password = sentinel });
            Check(failedLogin.StatusCode == HttpStatusCode.Unauthorized, "login failure");
            Check(failedLogin.Headers.Contains("X-Trace-Id"), "trace header");
            await client.GetAsync("/api/products?search=" + Uri.EscapeDataString(sentinel));
            // Synthetic audit fixtures exercise the monitoring threshold without exhausting the shared auth limiter.
            await Execute("""
                INSERT INTO audit_events (event, severity, remote_ip, trace_id, method, route, status_code, elapsed_ms)
                SELECT 'LoginFailed', 'Warning', '127.0.0.1', 'threshold-fixture', 'POST', 'api/auth/login', 401, 1
                FROM generate_series(1,5)
                """);
            As(admin);
            using var summary = JsonDocument.Parse(await client.GetStringAsync("/api/monitoring/summary"));
            Check(summary.RootElement.GetProperty("alerts").EnumerateArray().Any(a => a.GetProperty("eventName").GetString() == "LoginFailed"), "repeated login alert");
            using var events = JsonDocument.Parse(await client.GetStringAsync("/api/monitoring/events?eventName=LoginFailed&pageSize=100"));
            Check(events.RootElement.EnumerateArray().All(e => e.GetProperty("event").GetString() == "LoginFailed"), "filtered admin view");
            Check((await client.GetAsync("/api/monitoring/events?pageSize=101")).StatusCode == HttpStatusCode.BadRequest, "bounded query");
            Check((await client.DeleteAsync("/api/monitoring/events")).StatusCode == HttpStatusCode.MethodNotAllowed, "no log deletion endpoint");

            await Execute("ALTER TABLE cart_items RENAME TO cart_items_logging_probe");
            try
            {
                using var failed = await client.GetAsync("/api/cart");
                Check(failed.StatusCode == HttpStatusCode.InternalServerError, "controlled server error");
                var body = await failed.Content.ReadAsStringAsync();
                Check(!body.Contains("cart_items") && !body.Contains("Npgsql"), "exception details not exposed");
                Check(body.Contains(failed.Headers.GetValues("X-Trace-Id").Single()), "error correlation");
            }
            finally { await Execute("ALTER TABLE cart_items_logging_probe RENAME TO cart_items"); }

            await Execute("ALTER TABLE audit_events RENAME TO audit_events_logging_probe");
            try
            {
                using var request = await client.DeleteAsync($"/api/cart/items/{Guid.NewGuid()}");
                Check(request.StatusCode == HttpStatusCode.NotFound, "audit failure does not fake rollback of completed request");
                var unhealthy = false;
                for (var i = 0; i < 50; i++)
                {
                    unhealthy = (await client.GetAsync("/health/db")).StatusCode == HttpStatusCode.ServiceUnavailable;
                    if (unhealthy) break;
                    await Task.Delay(100);
                }
                Check(unhealthy, "audit storage failure changes health status");
            }
            finally { await Execute("ALTER TABLE audit_events_logging_probe RENAME TO audit_events"); }
            As(null); await client.GetAsync("/api/auth/me");
            await Task.Delay(100);
            Check((await client.GetAsync("/health/db")).IsSuccessStatusCode, "audit recovery restores health");
            string logs = "";
            foreach (var path in Directory.GetFiles(directory, "*.jsonl"))
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream);
                logs += await reader.ReadToEndAsync();
            }
            Check(logs.Contains("LoginFailed") && logs.Contains("Audit storage failed"), "persistent app and audit failure logs");
            Check(!logs.Contains(sentinel) && !logs.Contains(customer) && !logs.Contains(admin) && !logs.Contains("audit-test@example.com"), "secret redaction in file logs");
            As(admin);
            var auditJson = await client.GetStringAsync("/api/monitoring/events?pageSize=100");
            Check(!auditJson.Contains(sentinel) && !auditJson.Contains(admin), "secret redaction in audit events");
        }
        finally { client.DefaultRequestHeaders.Authorization = original; }
        Console.WriteLine("PASS: log authorization/filtering, suspicious-activity alerts, trace correlation, sanitized persistent logging, controlled errors and audit-storage failure/recovery.");
    }
}
