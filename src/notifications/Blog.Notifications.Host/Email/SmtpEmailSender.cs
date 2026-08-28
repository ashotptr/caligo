using System.Text;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;
using MimeKit.Text;
using Blog.Notifications.Core;
using Blog.Notifications.Host.Messaging;

namespace Blog.Notifications.Host.Email;

public class SmtpOptions
{
    public const string SectionName = "Smtp";

    public string Host { get; set; } = string.Empty;

    public int Port { get; set; } = 1025;

    public string? Username { get; set; }

    public string? Password { get; set; }

    public bool UseStartTls { get; set; }

    public string FromAddress { get; set; } = "blog@localhost";

    public string FromName { get; set; } = "Blog";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Host);
}

public class SmtpEmailSender : IEmailSender
{
    private readonly SmtpOptions _smtp;
    private readonly NotificationsOptions _notifications;
    private readonly ILogger<SmtpEmailSender> _logger;

    public SmtpEmailSender(
        IOptions<SmtpOptions> smtp,
        IOptions<NotificationsOptions> notifications,
        ILogger<SmtpEmailSender> logger)
    {
        _smtp = smtp.Value;
        _notifications = notifications.Value;
        _logger = logger;
    }

    public bool IsConfigured => _smtp.IsConfigured;

    public async Task SendPostPublishedAsync(SendEmailCommand command, CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            // Thrown rather than ignored: a command that cannot be executed must travel the
            // retry ladder and end up visible, not disappear into a successful no-op.
            throw new InvalidOperationException("No SMTP host is configured.");
        }

        var postUrl = $"{_notifications.SiteUrl.TrimEnd('/')}/posts/{command.PostId}";
        var unsubscribeUrl =
            $"{_notifications.SiteUrl.TrimEnd('/')}/api/subscriptions/unsubscribe/{command.UnsubscribeToken}";

        var message = new MimeMessage();

        message.From.Add(new MailboxAddress(_smtp.FromName, _smtp.FromAddress));
        message.To.Add(new MailboxAddress(command.DisplayName ?? command.Email, command.Email));
        message.Subject = $"New post: {command.PostTitle}";

        message.Body = new TextPart(TextFormat.Html)
        {
            Text =
                "<p>A new post is up:</p>" +
                $"<p><a href=\"{postUrl}\"><strong>{command.PostTitle}</strong></a></p>" +
                "<hr>" +
                $"<p style=\"font-size:12px;color:#666\"><a href=\"{unsubscribeUrl}\">Unsubscribe</a></p>"
        };

        // Lets a mail client offer one-click unsubscribe, which is what keeps announcement mail
        // out of spam folders and is expected of anything sending in bulk.
        //
        // Written through SetValue rather than Headers.Add because MimeKit formats a header
        // value at the moment it is added, using the default 78-character line limit. An
        // unsubscribe URL exceeds that, so Headers.Add would fold it onto a continuation line -
        // valid RFC 5322, but mishandled by enough parsers in the wild that it is not worth
        // relying on. Passing format options to SendAsync does not help: by then the value has
        // already been formatted. 998 is the hard line limit from the same RFC.
        var unfolded = FormatOptions.Default.Clone();
        unfolded.MaxLineLength = 998;

        var unsubscribeHeader = new Header("List-Unsubscribe", string.Empty);
        unsubscribeHeader.SetValue(unfolded, Encoding.UTF8, $"<{unsubscribeUrl}>");

        message.Headers.Add(unsubscribeHeader);

        using var client = new SmtpClient();

        await client.ConnectAsync(
            _smtp.Host,
            _smtp.Port,
            _smtp.UseStartTls ? SecureSocketOptions.StartTls : SecureSocketOptions.None,
            cancellationToken);

        if (!string.IsNullOrWhiteSpace(_smtp.Username))
        {
            await client.AuthenticateAsync(_smtp.Username, _smtp.Password, cancellationToken);
        }

        await client.SendAsync(message, cancellationToken);
        await client.DisconnectAsync(quit: true, cancellationToken);

        _logger.LogDebug("Delivered {DeliveryId} to {Email}.", command.DeliveryId, command.Email);
    }
}
