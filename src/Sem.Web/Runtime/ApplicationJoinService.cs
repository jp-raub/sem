using System.ComponentModel.DataAnnotations;
using System.Net.Mail;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Sem.Web.Data;

namespace Sem.Web.Runtime;

/// <summary>Shared invitation registration and one-use email confirmation for legacy and app join routes.</summary>
public sealed class ApplicationJoinService(ApplicationDbContext db)
{
    public const string QueuedMessage = "確認メールを送信待ちに登録しました。到着後、リンクを開いてパスキーを登録してください。";
    public const string InvalidTicketMessage = "確認リンクは無効・期限切れ・使用済みです。";
    public const string EnrollmentMessage = "メール確認が完了しました。この画面で期限内にパスキーを登録してください。";

    public Task<string> BeginLegacyRegistrationAsync(UserManager<ApplicationUser> users,
        NotificationService notifications, DeploymentSettings settings, string name, string email,
        Func<string> readInvite, CancellationToken ct = default) =>
        BeginRegistrationAsync(users, notifications, settings, readInvite, name, email, appJoin: false, ct);

    public Task<string> BeginApplicationJoinAsync(UserManager<ApplicationUser> users,
        NotificationService notifications, DeploymentSettings settings, string invite, string name, string email,
        CancellationToken ct = default) =>
        BeginRegistrationAsync(users, notifications, settings, () => invite, name, email, appJoin: true, ct);

    private async Task<string> BeginRegistrationAsync(UserManager<ApplicationUser> users,
        NotificationService notifications, DeploymentSettings settings, Func<string> readInvite,
        string name, string email, bool appJoin, CancellationToken ct)
    {
        if (!notifications.EmailConfigured) throw new ValidationException("SMTPを設定してから登録してください。");
        email = email.Trim().ToLowerInvariant();
        name = name.Trim();
        if (email.Length > 254 || email.Any(char.IsControl) || !new EmailAddressAttribute().IsValid(email))
            throw new ValidationException("メールアドレスを確認してください。");
        if (name.Length is < 1 or > 60) throw new ValidationException("入力項目を確認してください：name");

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var site = await db.SiteStates.FindAsync(1);
        if (site?.Initialized != true) throw new ValidationException("初期設定が必要です。");

        var inviteToken = readInvite().Trim();
        if (inviteToken.Length is < 1 or > 128) throw new ValidationException("入力項目を確認してください：invite");
        var tokenHash = SemEndpoints.Hash(inviteToken);
        var invite = await db.Invitations.FirstOrDefaultAsync(i => i.TokenHash == tokenHash &&
            !i.Disabled && i.Expires > SemEndpoints.Now && i.Uses < i.Limit, ct);
        if (invite is null) throw new ValidationException("招待リンクが失効しています。");

        var pendingUser = await users.FindByEmailAsync(email);
        if (pendingUser is not null && (pendingUser.Enabled || pendingUser.Bootstrap || pendingUser.InvitationId != invite.Id))
            throw new ValidationException("そのアドレスでは登録処理を開始できません。");
        if (pendingUser is null)
        {
            pendingUser = new ApplicationUser
            {
                Email = email,
                UserName = email,
                DisplayName = name,
                Enabled = false,
                IsAdmin = false,
                Bootstrap = false,
                InvitationId = invite.Id
            };
            var created = await users.CreateAsync(pendingUser);
            if (!created.Succeeded) throw new ValidationException("登録情報を確認してください。");
        }
        else
        {
            await db.EmailTickets.Where(t => t.UserId == pendingUser.Id && !t.Used)
                .ExecuteUpdateAsync(update => update.SetProperty(t => t.Used, true), ct);
        }

        var token = SemEndpoints.Secret();
        db.EmailTickets.Add(new EmailTicket
        {
            UserId = pendingUser.Id,
            Hash = SemEndpoints.Hash(token),
            Expires = SemEndpoints.Now + 3600
        });
        var relativeLink = appJoin
            ? "join#token=" + Uri.EscapeDataString(token)
            : "verify?token=" + Uri.EscapeDataString(token);
        var link = new Uri(new Uri(settings.PublicUrl), relativeLink).ToString();
        notifications.Email(db, pendingUser.Id, "SEM メールアドレス確認",
            "次のリンクでメールを確認し、パスキーを登録してください。\n" + link);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return QueuedMessage;
    }

    /// <summary>Consumes the existing email ticket, confirms the email, and issues the existing temporary enrollment cookie.</summary>
    public async Task<ApplicationJoinConfirmationResult> ConfirmAndIssueEnrollmentAsync(HttpContext context,
        string token, CancellationToken ct = default)
    {
        var hash = SemEndpoints.Hash(token);
        var ticket = await db.EmailTickets.FirstOrDefaultAsync(t => t.Hash == hash && !t.Used &&
            t.Expires > SemEndpoints.Now, ct);
        if (ticket is null) return ApplicationJoinConfirmationResult.InvalidTicket;

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var changed = await db.EmailTickets.Where(t => t.Id == ticket.Id && !t.Used &&
                t.Expires > SemEndpoints.Now)
            .ExecuteUpdateAsync(update => update.SetProperty(t => t.Used, true), ct);
        if (changed != 1) return ApplicationJoinConfirmationResult.RejectedAfterLookup;

        var user = await db.Users.FindAsync([ticket.UserId], ct);
        if (user is null || user.Enabled) return ApplicationJoinConfirmationResult.RejectedAfterLookup;
        user.EmailConfirmed = true;
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, user.Id)], "SEM.Enrollment"));
        await context.SignInAsync("SEM.Enrollment", principal, new AuthenticationProperties
        {
            IsPersistent = false,
            ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(20)
        });
        return ApplicationJoinConfirmationResult.Confirmed;
    }
}

public enum ApplicationJoinConfirmationResult
{
    InvalidTicket,
    RejectedAfterLookup,
    Confirmed
}
