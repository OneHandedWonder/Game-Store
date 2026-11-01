using System.Collections.Concurrent;
using System.Security.Claims;
using System.Text.Json;
using Game_Store_API.Auth;
using Game_Store_API.Controllers;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

static class OrderConfirmationChecks
{
    public static async Task RunAsync(NpgsqlDataSource source, IDataProtectionProvider protection)
    {
        static void Check(bool ok, string name) { if (!ok) throw new Exception("Order confirmation: " + name); }
        async Task<object?> Sql(string sql, params object[] values)
        {
            await using var command = source.CreateCommand(sql);
            foreach (var value in values) command.Parameters.AddWithValue(value);
            return await command.ExecuteScalarAsync();
        }
        var user = Guid.NewGuid();
        var version = Guid.NewGuid();
        var product = Guid.NewGuid();
        const string email = "CONFIRMATION@EXAMPLE.COM";
        await Sql("INSERT INTO users (id,email,password_hash,email_verified,security_version) VALUES ($1,$2,'unused',true,$3)", user, email, version);
        await Sql("INSERT INTO products (id,name,description,price,stock) VALUES ($1,'Confirmation game','',12.50,5)", product);
        var controller = new OrdersController(source, protection)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([
                    new Claim("sub", user.ToString()), new Claim("security_version", version.ToString())], "Test")) }
            }
        };
        CheckoutRequest Request(Guid key, string card = "4242424242424242") => new()
        {
            DeliveryAddress = new Game_Store_API.Data.DeliveryAddress("Demo Recipient", "Example Street 42", "Suite 2", "1234", "Demo City", "DK"),
            CheckoutKey = key, ExpectedTotal = 12.50m, Items = [new(product, 1, 12.50m)],
            CardNumber = card, CardholderName = "Not for email", ExpiryMonth = 12,
            ExpiryYear = DateTime.UtcNow.Year + 1, SecurityCode = "938",
        };
        try
        {
            var first = Request(Guid.NewGuid());
            await Sql("INSERT INTO cart_items (user_id,product_id,quantity) VALUES ($1,$2,1)", user, product);
            Check(await controller.Checkout(first, default) is CreatedAtActionResult, "successful order");
            Check(await controller.Checkout(first, default) is OkObjectResult, "idempotent retry");
            Check((long)(await Sql("SELECT count(*) FROM email_outbox WHERE user_id=$1", user))! == 1, "one confirmation for retries");
            await Sql("INSERT INTO cart_items (user_id,product_id,quantity) VALUES ($1,$2,1)", user, product);
            Check(await controller.Checkout(Request(Guid.NewGuid()), default) is CreatedAtActionResult, "second order");
            Check((long)(await Sql("SELECT count(*) FROM email_outbox WHERE user_id=$1 AND purpose='order'", user))! == 2, "multiple orders retain separate mail");

            var protector = protection.CreateProtector("GameStore.AccountEmail.v1");
            await using (var query = source.CreateCommand("SELECT protected_message, order_id FROM email_outbox WHERE user_id=$1"))
            {
                query.Parameters.AddWithValue(user);
                await using var reader = await query.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    var payload = reader.GetString(0);
                    Check(!payload.Contains(email) && !payload.Contains("Confirmation game"), "encrypted payload");
                    var mail = JsonSerializer.Deserialize<AccountEmail>(protector.Unprotect(payload))!;
                    Check(mail.Recipient == email && mail.Subject.Contains(reader.GetGuid(1).ToString()), "account recipient and order reference");
                    Check(mail.Body.Contains("1 x Confirmation game") && mail.Body.Contains("Total: 12,50 DKK") && mail.Body.Contains("No money was charged"), "snapshot and simulated payment disclosure");
                    Check(!mail.Body.Contains(first.CardNumber) && !mail.Body.Contains(first.SecurityCode) && !mail.Body.Contains(first.CardholderName), "payment details excluded");
                }
            }
            await Sql("INSERT INTO cart_items (user_id,product_id,quantity) VALUES ($1,$2,1)", user, product);
            Check(await controller.Checkout(Request(Guid.NewGuid(), "4000000000000002"), default) is BadRequestObjectResult, "declined checkout");
            Check((long)(await Sql("SELECT count(*) FROM email_outbox WHERE user_id=$1", user))! == 2, "no mail for declined checkout");
            await Sql("ALTER TABLE email_outbox ADD CONSTRAINT confirmation_failure_probe CHECK (purpose <> 'order') NOT VALID");
            try
            {
                var failed = false;
                try { await controller.Checkout(Request(Guid.NewGuid()), default); }
                catch (PostgresException error) when (error.SqlState == PostgresErrorCodes.CheckViolation) { failed = true; }
                Check(failed, "queue insertion failure is explicit");
            }
            finally { await Sql("ALTER TABLE email_outbox DROP CONSTRAINT confirmation_failure_probe"); }
            Check((long)(await Sql("SELECT count(*) FROM orders WHERE user_id=$1", user))! == 2 &&
                (int)(await Sql("SELECT stock FROM products WHERE id=$1", product))! == 3 &&
                (long)(await Sql("SELECT count(*) FROM cart_items WHERE user_id=$1", user))! == 1,
                "queue failure rolls back order, stock and cart");

            using (var failing = new EmailOutboxWorker(source, protection, new RejectMail(), NullLogger<EmailOutboxWorker>.Instance))
            {
                await failing.StartAsync(default);
                for (var i = 0; i < 100 && (long)(await Sql("SELECT count(*) FROM email_outbox WHERE user_id=$1 AND attempts=1", user))! < 2; i++) await Task.Delay(50);
                await failing.StopAsync(default);
                Check((long)(await Sql("SELECT count(*) FROM email_outbox WHERE user_id=$1 AND attempts=1", user))! == 2, "SMTP failure retains confirmations for retry");
            }
            await Sql("UPDATE email_outbox SET next_attempt_at=now() WHERE user_id=$1", user);
            var capture = new CaptureMail();
            using (var worker = new EmailOutboxWorker(source, protection, capture, NullLogger<EmailOutboxWorker>.Instance))
            {
                await worker.StartAsync(default);
                for (var i = 0; i < 100 && (long)(await Sql("SELECT count(*) FROM email_outbox WHERE user_id=$1", user))! > 0; i++) await Task.Delay(50);
                await worker.StopAsync(default);
                Check(capture.Messages.Count == 2 && (long)(await Sql("SELECT count(*) FROM email_outbox WHERE user_id=$1", user))! == 0, "captured delivery removes queued confirmations");
            }
        }
        finally
        {
            await Sql("DELETE FROM email_outbox WHERE user_id=$1", user);
            await Sql("DELETE FROM order_items WHERE order_id IN (SELECT id FROM orders WHERE user_id=$1)", user);
            await Sql("DELETE FROM orders WHERE user_id=$1", user);
            await Sql("DELETE FROM users WHERE id=$1", user);
            await Sql("DELETE FROM products WHERE id=$1", product);
        }
        Console.WriteLine("PASS: encrypted order confirmations, trusted recipient/snapshots, no card data, retry deduplication, multiple pending orders, transactional rollback and captured mail retries.");
    }

    private sealed class RejectMail : IAccountEmailSender
    {
        public Task SendAsync(AccountEmail message, CancellationToken cancellationToken) => throw new IOException("Simulated SMTP outage.");
    }
    private sealed class CaptureMail : IAccountEmailSender
    {
        public ConcurrentQueue<AccountEmail> Messages { get; } = new();
        public Task SendAsync(AccountEmail message, CancellationToken cancellationToken) { Messages.Enqueue(message); return Task.CompletedTask; }
    }
}
