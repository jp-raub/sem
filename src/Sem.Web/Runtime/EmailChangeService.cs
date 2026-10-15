using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Sem.Web.Data;

namespace Sem.Web.Runtime;

public interface IEmailChangePasskeyProof
{
    Task<string> Options(ApplicationUser user);
    Task<bool> Verify(ApplicationUser user, string credential);
}
// The production registration is exclusively native Identity WebAuthn assertion verification.
public sealed class NativeEmailChangePasskeyProof(SignInManager<ApplicationUser> signin, UserManager<ApplicationUser> users) : IEmailChangePasskeyProof
{
    public Task<string> Options(ApplicationUser user) => signin.MakePasskeyRequestOptionsAsync(user);
    public async Task<bool> Verify(ApplicationUser user, string credential)
    {
        var result = await signin.PerformPasskeyAssertionAsync(credential);
        if (!result.Succeeded || result.User.Id != user.Id) return false;
        return (await users.AddOrUpdatePasskeyAsync(user, result.Passkey)).Succeeded;
    }
}

public static class EmailChangePolicy
{
    public const long ReauthenticationSeconds = 300, ConfirmationSeconds = 900;
    public static List<long> Recent(string json, long now, long window) =>
        (JsonSerializer.Deserialize<long[]>(json) ?? []).Where(t => t > now - window).Order().ToList();
    public static void RequireFailuresAvailable(string json, long now)
    {
        var recent = Recent(json, now, 900);
        if (recent.Count >= 5) throw new EmailChangeRateLimitException(recent[0] + 900 - now);
    }
    public static string Failure(string json, long now) => JsonSerializer.Serialize(Recent(json, now, 900).Append(now));
    public static void AdmitSend(EmailChangeRateState state, long now)
    {
        var times = Recent(state.SendTimes, now, 3600);
        if (times.Count >= 3) throw new EmailChangeRateLimitException(times[0] + 3600 - now);
        if (times.Count > 0 && times[^1] + 60 > now) throw new EmailChangeRateLimitException(times[^1] + 60 - now);
        state.SendTimes = JsonSerializer.Serialize(times.Append(now));
        state.RowVersion = checked(state.RowVersion + 1);
    }
}

public sealed class EmailChangeService(ApplicationDbContext db, UserManager<ApplicationUser> users,
    IEmailChangePasskeyProof proof, NotificationService notifications, DeploymentSettings settings,
    IDataProtectionProvider protection, TimeProvider clock, IOptions<IdentityOptions> identityOptions)
{
    readonly IDataProtector targetProtector = protection.CreateProtector("SEM.EmailChange.Target.v1");
    readonly IDataProtector mailProtector = protection.CreateProtector("SEM.EmailChange.Mail.v1");
    long Now => clock.GetUtcNow().ToUnixTimeSeconds();

    public async Task<ApplicationUser> Actor(ClaimsPrincipal principal, CancellationToken ct = default)
    {
        var id = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        var stamp = principal.FindFirstValue(identityOptions.Value.ClaimsIdentity.SecurityStampClaimType);
        var session = principal.FindFirstValue(PasskeyAdditionService.SessionClaim);
        var user = id is null ? null : await db.Users.SingleOrDefaultAsync(x => x.Id == id, ct);
        if (user is not null) await db.Entry(user).ReloadAsync(ct);
        if (principal.Identity?.IsAuthenticated != true || user is null || !user.Enabled || !user.EmailConfirmed ||
            string.IsNullOrEmpty(user.Email) || string.IsNullOrEmpty(stamp) || stamp != user.SecurityStamp ||
            string.IsNullOrEmpty(session) || await db.RevokedAuthSessions.AnyAsync(x => x.TokenHash == SemEndpoints.Hash(session), ct))
            throw new UnauthorizedAccessException();
        return user;
    }
    public async Task<object> Status(ClaimsPrincipal principal, CancellationToken ct = default)
    {
        var user = await Actor(principal, ct);
        var session = Session(principal);
        var op = await db.EmailChangeOperations.AsNoTracking().SingleOrDefaultAsync(x => x.UserId == user.Id, ct);
        object? pending = op is { Status: "Pending" } && op.ExpiresAt > Now && op.SecurityStamp == user.SecurityStamp &&
            op.SessionHash == session && op.OriginalEmailHash == EmailHash(user.Email!)
            ? new { operationId = op.Id, expiresAt = DateTimeOffset.FromUnixTimeSeconds(op.ExpiresAt).ToString("O"), lastSentAt = op.LastSentAt is long sent ? DateTimeOffset.FromUnixTimeSeconds(sent).ToString("O") : null, confirmationSent = op.LastSentAt is not null } : null;
        return new { actorId = user.Id, pending };
    }
    public async Task<object> Begin(ClaimsPrincipal principal, string newEmail, CancellationToken ct = default)
    {
        if (!notifications.EmailConfigured) throw new ValidationException("SMTPを設定してからメール変更を開始してください。");
        newEmail = newEmail.Trim();
        if (newEmail.Length is < 3 or > 254 || newEmail.Any(char.IsControl) || !new EmailAddressAttribute().IsValid(newEmail))
            throw new ValidationException("新しいメールアドレスを確認してください。");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var user = await Actor(principal, ct);
        var previous = await db.EmailChangeOperations.SingleOrDefaultAsync(x => x.UserId == user.Id, ct);
        if (previous is { Status: "Pending" } && previous.ExpiresAt > Now)
            throw new ValidationException("確認待ちの変更があります。完了・期限切れ・取消後にやり直してください。");
        if (EmailHash(user.Email!) == EmailHash(newEmail))
            throw new ValidationException("このメールアドレスへの変更を開始できません。");
        var rate = await Rate(user.Id, ct);
        EmailChangePolicy.RequireFailuresAvailable(rate.PasskeyFailures, Now);
        EmailChangePolicy.RequireFailuresAvailable(rate.VerificationFailures, Now);
        if (previous is not null) { db.EmailChangeOperations.Remove(previous); await db.SaveChangesAsync(ct); }
        // Release expired reservations for this target atomically; old capabilities remain invalid.
        var targetHash = EmailHash(newEmail);
        await db.EmailChangeOperations.Where(x => x.TargetEmailHash == targetHash && x.ExpiresAt <= Now)
            .ExecuteUpdateAsync(set => set.SetProperty(x => x.TargetEmailHash, (string?)null)
                .SetProperty(x => x.TokenHash, (string?)null).SetProperty(x => x.ChallengeHash, (string?)null)
                .SetProperty(x => x.ProtectedTargetEmail, "").SetProperty(x => x.Status, "Invalidated")
                .SetProperty(x => x.RowVersion, x => x.RowVersion + 1), ct);
        var options = JsonNode.Parse(await proof.Options(user))!.AsObject();
        var op = new EmailChangeOperation { UserId = user.Id, OriginalEmailHash = EmailHash(user.Email!),
            TargetEmailHash = EmailHash(newEmail), ProtectedTargetEmail = targetProtector.Protect(newEmail),
            SecurityStamp = user.SecurityStamp!, SessionHash = Session(principal), CreatedAt = Now,
            ExpiresAt = Now + 300, ChallengeExpiresAt = Now + 300,
            ChallengeHash = SemEndpoints.Hash(options["challenge"]!.GetValue<string>()) };
        db.EmailChangeOperations.Add(op);
        Audit(user.Id, "EmailChangeStarted", op.Id);
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        return new { operationId = op.Id, publicKey = options };
    }
    public async Task<object> Options(ClaimsPrincipal principal, string id, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var (user, op) = await Require(principal, id, ct);
        var rate = await Rate(user.Id, ct);
        EmailChangePolicy.RequireFailuresAvailable(rate.PasskeyFailures, Now);
        var options = JsonNode.Parse(await proof.Options(user))!.AsObject();
        op.ChallengeHash = SemEndpoints.Hash(options["challenge"]!.GetValue<string>());
        op.ChallengeExpiresAt = Math.Min(op.ExpiresAt, Now + 300);
        op.RowVersion = checked(op.RowVersion + 1);
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        return new { operationId = op.Id, publicKey = options };
    }
    public async Task Reauthenticate(ClaimsPrincipal principal, string id, string credential, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var (user, op) = await Require(principal, id, ct);
        var rate = await Rate(user.Id, ct);
        EmailChangePolicy.RequireFailuresAvailable(rate.PasskeyFailures, Now);
        bool verified = false;
        if (op.ChallengeExpiresAt > Now && ChallengeMatches(credential, op.ChallengeHash))
        {
            try { verified = await proof.Verify(user, credential); }
            catch (Exception e) when (e is JsonException or FormatException or ArgumentException or ValidationException) { verified = false; }
        }
        if (!verified)
        {
            rate.PasskeyFailures = EmailChangePolicy.Failure(rate.PasskeyFailures, Now);
            rate.RowVersion = checked(rate.RowVersion + 1);
            op.ChallengeHash = null; op.RowVersion = checked(op.RowVersion + 1);
            // A new challenge/operation cannot clear the account-and-purpose failure window.
            await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
            throw new ValidationException("既存パスキーで再認証できませんでした。本人確認をやり直してください。");
        }
        // Recheck the live principal after the native assertion and before queueing.
        user = await Actor(principal, ct);
        if (user.SecurityStamp != op.SecurityStamp || EmailHash(user.Email!) != op.OriginalEmailHash)
            throw new UnauthorizedAccessException();
        EmailChangePolicy.AdmitSend(rate, Now);
        if (!notifications.EmailConfigured) throw new ValidationException("SMTPが設定されていません。");
        if (op.LastSentAt is null) op.ExpiresAt = checked(Now + EmailChangePolicy.ConfirmationSeconds);
        var token = SemEndpoints.Secret();
        op.TokenHash = SemEndpoints.Hash(token); op.ReauthenticatedAt = Now; op.LastSentAt = Now;
        op.ChallengeHash = null; op.RowVersion = checked(op.RowVersion + 1);
        // Cancel unsent superseded proof mail; an already delivered/claimed link is rejected by the new hash.
        await db.EmailChangeMails.Where(x => x.OperationId == op.Id && x.Kind == "Verification" && x.Status == "Pending")
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, "Cancelled").SetProperty(x => x.ProtectedPayload, ""), ct);
        var recipient = targetProtector.Unprotect(op.ProtectedTargetEmail);
        var link = new Uri(new Uri(settings.PublicUrl), "profile/email-change-confirm#op=" + op.Id + "&token=" + token);
        Queue(user.Id, op.Id, "Verification", recipient, "SEM メールアドレス変更の確認",
            "次の専用リンクで新しいメールアドレスを確認してください。ログイン権限は発行されません。期限は最初の送信から15分、再送しても延長されません。\n" + link, op.ExpiresAt);
        Audit(user.Id, "EmailChangeVerificationQueued", op.Id);
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
    }
    public async Task Cancel(ClaimsPrincipal principal, string id, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var user = await Actor(principal, ct);
        var op = await db.EmailChangeOperations.SingleOrDefaultAsync(x => x.Id == id && x.UserId == user.Id, ct);
        if (op is null || op.SessionHash != Session(principal)) throw new UnauthorizedAccessException();
        Invalidate(op, "Cancelled"); Audit(user.Id, "EmailChangeCancelled", op.Id);
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
    }
    public async Task Confirm(string id, string token, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var op = await db.EmailChangeOperations.SingleOrDefaultAsync(x => x.Id == id, ct);
        if (op is null) throw new ValidationException("確認リンクは無効・期限切れ・使用済みです。");
        var rate = await Rate(op.UserId, ct);
        EmailChangePolicy.RequireFailuresAvailable(rate.VerificationFailures, Now);
        var valid = op.Status == "Pending" && op.LastSentAt is not null && op.ExpiresAt > Now &&
            op.TokenHash is not null && FixedEqual(SemEndpoints.Hash(token), op.TokenHash);
        if (!valid)
        {
            rate.VerificationFailures = EmailChangePolicy.Failure(rate.VerificationFailures, Now);
            rate.RowVersion = checked(rate.RowVersion + 1);
            if (op.Status == "Pending" && EmailChangePolicy.Recent(rate.VerificationFailures, Now, 900).Count >= 5) Invalidate(op, "Invalidated");
            await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
            throw new ValidationException("確認リンクは無効・期限切れ・使用済みです。");
        }
        var user = await db.Users.SingleOrDefaultAsync(x => x.Id == op.UserId, ct);
        if (user is null || !user.Enabled || !user.EmailConfirmed || user.SecurityStamp != op.SecurityStamp ||
            string.IsNullOrEmpty(user.Email) || EmailHash(user.Email) != op.OriginalEmailHash)
            throw new ValidationException("この変更は無効になりました。本人確認からやり直してください。");
        var recipient = targetProtector.Unprotect(op.ProtectedTargetEmail);
        if (await users.FindByEmailAsync(recipient) is not null)
            throw new ValidationException("メール変更を完了できません。変更を取り消し、本人確認からやり直してください。");
        var oldEmail = user.Email;
        user.Email = recipient; user.UserName = recipient; user.EmailConfirmed = true;
        user.SecurityStamp = Guid.NewGuid().ToString("N");
        var updated = await users.UpdateAsync(user);
        if (!updated.Succeeded) throw new ValidationException("メール変更を完了できませんでした。");
        if (!await db.RevokedAuthSessions.AnyAsync(x => x.TokenHash == op.SessionHash, ct))
            db.RevokedAuthSessions.Add(new() { TokenHash = op.SessionHash, UserId = user.Id,
                RevokedAt = clock.GetUtcNow().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'") });
        Invalidate(op, "Completed");
        Queue(user.Id, op.Id, "OldAddressNotice", oldEmail, "SEM メールアドレス変更完了",
            "アカウントのメールアドレスが変更されました。本人の操作でない場合は管理者へ連絡してください。", Now + 7 * 86400);
        Audit(user.Id, "EmailChangeCompleted", op.Id);
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        // No SignIn/RefreshSignIn: the bearer capability authorizes this mutation only.
    }
    async Task<(ApplicationUser, EmailChangeOperation)> Require(ClaimsPrincipal principal, string id, CancellationToken ct)
    {
        var user = await Actor(principal, ct);
        var op = await db.EmailChangeOperations.SingleOrDefaultAsync(x => x.Id == id && x.UserId == user.Id, ct);
        if (op is null || op.Status != "Pending" || op.SessionHash != Session(principal) ||
            op.SecurityStamp != user.SecurityStamp || op.OriginalEmailHash != EmailHash(user.Email!)) throw new UnauthorizedAccessException();
        if (op.ExpiresAt <= Now) throw new ValidationException("変更操作は期限切れです。本人確認からやり直してください。");
        return (user, op);
    }
    async Task<EmailChangeRateState> Rate(string userId, CancellationToken ct)
    {
        var state = await db.EmailChangeRateStates.SingleOrDefaultAsync(x => x.UserId == userId, ct);
        if (state is not null) return state;
        state = new() { UserId = userId }; db.EmailChangeRateStates.Add(state); return state;
    }
    void Queue(string userId, string operation, string kind, string recipient, string subject, string body, long expiry) =>
        db.EmailChangeMails.Add(new() { UserId = userId, OperationId = operation, Kind = kind, Due = Now, ExpiresAt = expiry,
            ProtectedPayload = mailProtector.Protect(JsonSerializer.Serialize(new EmailChangeMailPayload(recipient, subject, body))) });
    void Audit(string userId, string action, string id) => db.Audits.Add(new() { ActorId = userId, Action = action, EntityId = id, At = Now });
    static void Invalidate(EmailChangeOperation op, string status)
    { op.Status = status; op.TokenHash = null; op.ChallengeHash = null; op.TargetEmailHash = null; op.ProtectedTargetEmail = ""; op.RowVersion = checked(op.RowVersion + 1); }
    string EmailHash(string email) => SemEndpoints.Hash(users.NormalizeEmail(email)!);
    static string Session(ClaimsPrincipal principal) => SemEndpoints.Hash(principal.FindFirstValue(PasskeyAdditionService.SessionClaim) ?? throw new UnauthorizedAccessException());
    static bool FixedEqual(string left, string right) => CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(left), Encoding.UTF8.GetBytes(right));
    static bool ChallengeMatches(string credential, string? expected)
    {
        if (expected is null) return false;
        try
        {
            using var json = JsonDocument.Parse(credential);
            var encoded = json.RootElement.GetProperty("response").GetProperty("clientDataJSON").GetString()!.Replace('-', '+').Replace('_', '/');
            using var data = JsonDocument.Parse(Convert.FromBase64String(encoded.PadRight((encoded.Length + 3) / 4 * 4, '=')));
            return FixedEqual(SemEndpoints.Hash(data.RootElement.GetProperty("challenge").GetString()!), expected);
        }
        catch (Exception e) when (e is JsonException or FormatException or KeyNotFoundException or InvalidOperationException or ArgumentException) { return false; }
    }
}
