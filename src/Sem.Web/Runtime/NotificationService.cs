using System.Text.Json;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using MimeKit;
using Sem.Web.Data;
using WebPush;
namespace Sem.Web.Runtime;
public class NotificationService(IConfiguration cfg, IDataProtectionProvider dp)
{
    private readonly IDataProtector protector = dp.CreateProtector("SEM.Notifications.v1");
    public bool EmailConfigured => !string.IsNullOrWhiteSpace(cfg["Smtp:Host"]) && !string.IsNullOrWhiteSpace(cfg["Smtp:From"]);
    public bool PushConfigured => !string.IsNullOrWhiteSpace(cfg["Push:PublicKey"]) && !string.IsNullOrWhiteSpace(cfg["Push:PrivateKey"]);
    public string PublicKey => cfg["Push:PublicKey"] ?? "";
    public void Email(ApplicationDbContext db, string userId, string title, string body) => db.Deliveries.Add(new Delivery { UserId = userId, Subject = title, ProtectedBody = protector.Protect(body), Due = SemEndpoints.Now });
    public void Message(ApplicationDbContext db, string userId, string title, string body)
    {
        db.Messages.Add(new Message { UserId = userId, Title = title, Body = body, Created = SemEndpoints.Now });
        if (PushConfigured) db.Deliveries.Add(new Delivery { UserId = userId, Kind = "Push", Subject = title, Due = SemEndpoints.Now });
    }
    public Task SendBusinessEmail(string operationId, ApplicationUser user, string title, string body, CancellationToken ct) =>
        SendEmail(new Delivery { Id=operationId, UserId=user.Id, Subject=title, ProtectedBody=protector.Protect(body) },user,ct,
            SemEndpoints.Hash("business-email:"+operationId)+"@notifications.sem.invalid");
    public async Task SendEmail(Delivery delivery, ApplicationUser user, CancellationToken ct, string? messageId = null)
    {
        if (!EmailConfigured) throw new InvalidOperationException("SMTP not configured");
        var message = new MimeMessage(); message.From.Add(MailboxAddress.Parse(cfg["Smtp:From"]!)); message.To.Add(MailboxAddress.Parse(user.Email!)); message.Subject = delivery.Subject; message.Body = new TextPart("plain") { Text = protector.Unprotect(delivery.ProtectedBody) };
        if(messageId is not null) message.MessageId=messageId;
        using var client = new SmtpClient(); client.Timeout = 15000;
        var tls = cfg["Smtp:Tls"] ?? "StartTls";
        var security = tls switch { "SslOnConnect" => SecureSocketOptions.SslOnConnect, "None" when cfg.GetValue<bool>("Smtp:AllowInsecureLocalTest") => SecureSocketOptions.None, _ => SecureSocketOptions.StartTls };
        await client.ConnectAsync(cfg["Smtp:Host"]!, cfg.GetValue<int?>("Smtp:Port") ?? 587, security, ct);
        if (!string.IsNullOrEmpty(cfg["Smtp:Username"])) await client.AuthenticateAsync(cfg["Smtp:Username"]!, cfg["Smtp:Password"] ?? "", ct);
        await client.SendAsync(message, ct); await client.DisconnectAsync(true, ct);
    }
    public async Task SendPush(Delivery delivery, ApplicationDbContext db, CancellationToken ct)
    {
        if (!PushConfigured) throw new InvalidOperationException("Push not configured");
        using var client = new WebPushClient();
        var details = new VapidDetails(cfg["Push:Subject"] ?? "mailto:misc@raub.jp", PublicKey, cfg["Push:PrivateKey"]!);
        foreach (var device in await db.PushDevices.Where(d => d.UserId == delivery.UserId).ToListAsync(ct))
        {
            var owner=await db.Users.FindAsync([delivery.UserId],ct);if(owner is null||!await Sem.Web.Core.PushBinding.Valid(db,owner,device,ct))continue;
            try { await client.SendNotificationAsync(new PushSubscription(device.Endpoint, device.P256dh, device.Auth), JsonSerializer.Serialize(new { title = delivery.Subject, body = "新しいメッセージがあります", url = "/?messages=1" }), details, ct); }
            catch (WebPushException e) when ((int)e.StatusCode is 404 or 410) { db.PushDevices.Remove(device); }
        }
    }
}
public class NotificationWorker(IServiceScopeFactory scopes, ILogger<NotificationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(3));
        while (await timer.WaitForNextTickAsync(ct))
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>(); var service = scope.ServiceProvider.GetRequiredService<NotificationService>();
                var due = await db.Deliveries.Where(d => d.SentAt == null && d.Attempts < 5 && d.Due <= SemEndpoints.Now).OrderBy(d => d.Due).Take(20).ToListAsync(ct);
                foreach (var delivery in due)
                {
                    try
                    {
                        var user = await db.Users.FindAsync([delivery.UserId], ct); if (user is null) { delivery.SentAt = SemEndpoints.Now; continue; }
                        if (delivery.Kind == "Email") await service.SendEmail(delivery, user, ct);
                        else if (delivery.Kind == "Push") await service.SendPush(delivery, db, ct);
                        else if (delivery.Kind == "ScheduledTest") service.Message(db, user.Id, delivery.Subject, "バックグラウンドで予約通知が実行されました。");
                        else if (delivery.Kind == "Reminder") { var ev = await db.Events.FindAsync([delivery.Subject], ct); if (ev is not null && await db.Participations.AnyAsync(p => p.EventId == ev.Id && p.UserId == user.Id, ct)) { var expected = (ev.Opens ?? ev.Starts) - 7200; if (expected > SemEndpoints.Now) { delivery.Due = expected; continue; } service.Message(db, user.Id, "イベント開催前通知", ev.Title + "の開催前通知です。"); } }
                        delivery.SentAt = SemEndpoints.Now; delivery.Error = null;
                    }
                    catch (Exception e) when (e is not OperationCanceledException) { delivery.Attempts++; delivery.Error = e.GetType().Name; delivery.Due = SemEndpoints.Now + (long)Math.Pow(2, delivery.Attempts) * 15; logger.LogWarning("Notification delivery failed; type={Type}, attempt={Attempt}", delivery.Kind, delivery.Attempts); }
                }
                await db.SaveChangesAsync(ct);
            }
            catch (Exception e) when (e is not OperationCanceledException) { logger.LogWarning("Notification worker iteration failed ({Type})", e.GetType().Name); }
        }
    }
}
