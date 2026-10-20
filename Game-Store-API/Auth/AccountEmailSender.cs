using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;

namespace Game_Store_API.Auth;

public sealed class EmailSettings
{
    public string Host { get; init; } = "";
    public int Port { get; init; } = 587;
    public string Username { get; init; } = "";
    public string From { get; init; } = "";
    public string Password { get; init; } = "";
}

public sealed record AccountEmail(string Recipient, string Subject, string Body);

public interface IAccountEmailSender
{
    Task SendAsync(AccountEmail message, CancellationToken cancellationToken);
}

public sealed class AccountEmailSender(IOptions<EmailSettings> settings) : IAccountEmailSender
{
    public async Task SendAsync(AccountEmail message, CancellationToken cancellationToken)
    {
        var options = settings.Value;
        var mail = new MimeMessage();
        mail.From.Add(new MailboxAddress("Game Store", options.From));
        mail.To.Add(MailboxAddress.Parse(message.Recipient));
        mail.Subject = message.Subject;
        mail.Body = new TextPart("plain") { Text = message.Body };
        using var client = new SmtpClient { Timeout = 15_000 };
        await client.ConnectAsync(options.Host, options.Port, SecureSocketOptions.StartTls, cancellationToken);
        await client.AuthenticateAsync(options.Username, options.Password, cancellationToken);
        await client.SendAsync(mail, cancellationToken);
        await client.DisconnectAsync(true, cancellationToken);
    }
}
