using System.ComponentModel.DataAnnotations;
using System.Data;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Sem.Web.Data;

namespace Sem.Web.Runtime;

public class PasskeyAdditionService(ApplicationDbContext db, UserManager<ApplicationUser> users,
    SignInManager<ApplicationUser> signin, NotificationService notifications, TimeProvider clock)
{
    public const string SessionClaim = "sem.authentication-session";
    public const long OperationSeconds = 15 * 60;
    public const long RegistrationSeconds = 5 * 60;
    private long Now => clock.GetUtcNow().ToUnixTimeSeconds();

    public static long RegistrationDeadline(long expires, long confirmedAt) =>
        Math.Min(expires, checked(confirmedAt + RegistrationSeconds));

    public async Task<object?> StatusAsync(HttpContext context, ApplicationUser? user)
    {
        var session = context.User.FindFirstValue(SessionClaim);
        if (user is null || string.IsNullOrEmpty(session)) return null;
        var hash = SemEndpoints.Hash(session);
        var flow = await db.PasskeyAdditions.AsNoTracking().FirstOrDefaultAsync(p =>
            p.UserId == user.Id && p.SessionHash == hash && !p.Cancelled && p.CompletedAt == null &&
            p.Expires > Now && p.ReauthenticatedAt != null);
        if (flow is null || flow.Email != user.Email || flow.SecurityStamp != user.SecurityStamp ||
            flow.RegistrationUntil is long deadline && deadline <= Now) return null;
        return new { flow.Id, flow.Expires, emailVerified = flow.EmailVerifiedAt != null,
            flow.RegistrationUntil };
    }

    public async Task<JsonObject> BeginAsync(HttpContext context, ApplicationUser user)
    {
        if (!notifications.EmailConfigured) throw new ValidationException("SMTPを設定してから追加してください。");
        var hash = SessionHash(context);
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        await db.Entry(user).ReloadAsync();
        RequireOwner(user);
        await db.PasskeyAdditions.Where(p => p.UserId == user.Id && p.SessionHash == hash &&
            !p.Cancelled && p.CompletedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.Cancelled, true).SetProperty(p => p.EmailTokenHash, (string?)null));
        var options = await signin.MakePasskeyRequestOptionsAsync(user);
        var result = JsonNode.Parse(options)!.AsObject();
        var flow = new PasskeyAddition
        {
            UserId = user.Id, SessionHash = hash, Email = user.Email!,
            SecurityStamp = user.SecurityStamp!, Created = Now, Expires = Now + OperationSeconds,
            ReauthenticationChallengeHash = SemEndpoints.Hash(result["challenge"]!.GetValue<string>())
        };
        db.PasskeyAdditions.Add(flow);
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        result["operationId"] = flow.Id;
        return result;
    }

    public async Task ReauthenticateAsync(HttpContext context, ApplicationUser user, string id, string credential)
    {
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        var flow = await RequireAsync(context, user, id);
        if (flow.ReauthenticatedAt != null) throw new ValidationException("再認証は完了済みです。");
        CheckChallenge(credential, flow.ReauthenticationChallengeHash);
        var assertion = await signin.PerformPasskeyAssertionAsync(credential);
        if (!assertion.Succeeded || assertion.User.Id != user.Id || await users.IsLockedOutAsync(user))
            throw new ValidationException("既存パスキーで再認証できませんでした。");
        var saved = await users.AddOrUpdatePasskeyAsync(user, assertion.Passkey);
        if (!saved.Succeeded) throw new ValidationException("再認証結果を保存できませんでした。");
        flow.ReauthenticatedAt = Now;
        flow.Expires = Now + OperationSeconds;
        flow.ReauthenticationChallengeHash = null;
        QueueProofEmail(flow);
        Audit(user.Id, "PasskeyAdditionReauthenticated", flow.Id);
        await db.SaveChangesAsync();
        await tx.CommitAsync();
    }

    public async Task ResendAsync(HttpContext context, ApplicationUser user, string id)
    {
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        var flow = await RequireAsync(context, user, id);
        RequireReauthentication(flow);
        // All mutations remain under the existing global API rate limiter.
        // A resend invalidates old proof/creation state and never extends Expires.
        QueueProofEmail(flow);
        Audit(user.Id, "PasskeyAdditionEmailResent", flow.Id);
        await db.SaveChangesAsync();
        await tx.CommitAsync();
    }

    public async Task ConfirmEmailAsync(HttpContext context, ApplicationUser user, string token)
    {
        var hash = SemEndpoints.Hash(token);
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        var candidate = await db.PasskeyAdditions.FirstOrDefaultAsync(p => p.EmailTokenHash == hash);
        if (candidate is null) throw new ValidationException("確認コードは無効・使用済みです。");
        var flow = await RequireAsync(context, user, candidate.Id);
        RequireReauthentication(flow);
        if (flow.EmailVerifiedAt != null) throw new ValidationException("確認コードは使用済みです。");
        flow.EmailTokenHash = null;
        flow.EmailVerifiedAt = Now;
        flow.RegistrationUntil = RegistrationDeadline(flow.Expires, Now);
        flow.CreationChallengeHash = null;
        Audit(user.Id, "PasskeyAdditionEmailVerified", flow.Id);
        await db.SaveChangesAsync();
        await tx.CommitAsync();
    }

    public async Task<string> CreationOptionsAsync(HttpContext context, ApplicationUser user)
    {
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        var flow = await RequireAsync(context, user);
        RequireRegistration(flow);
        var options = await signin.MakePasskeyCreationOptionsAsync(new()
            { Id = user.Id, Name = user.UserName!, DisplayName = user.DisplayName });
        using var json = JsonDocument.Parse(options);
        flow.CreationChallengeHash = SemEndpoints.Hash(json.RootElement.GetProperty("challenge").GetString()!);
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        return options;
    }

    public async Task CompleteAsync(HttpContext context, ApplicationUser user, string credential)
    {
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        var flow = await RequireAsync(context, user);
        RequireRegistration(flow);
        CheckChallenge(credential, flow.CreationChallengeHash);
        var attestation = await signin.PerformPasskeyAttestationAsync(credential);
        if (!attestation.Succeeded || attestation.UserEntity.Id != user.Id)
            throw new ValidationException("追加するパスキーを確認できませんでした。");
        // The immediate SQLite transaction serializes competing completions.
        flow.CompletedAt = Now;
        flow.EmailTokenHash = null;
        flow.CreationChallengeHash = null;
        var saved = await users.AddOrUpdatePasskeyAsync(user, attestation.Passkey);
        if (!saved.Succeeded) throw new ValidationException("パスキーを保存できませんでした。");
        Audit(user.Id, "PasskeyAdded", flow.Id);
        notifications.Message(db, user.Id, "パスキーを追加しました", "本人の操作でない場合は管理者へ連絡してください。");
        notifications.Email(db, user.Id, "SEM パスキー追加完了", "パスキーを追加しました。本人の操作でない場合は管理者へ連絡してください。");
        await db.SaveChangesAsync();
        await tx.CommitAsync();
    }

    public async Task CancelAsync(HttpContext context, ApplicationUser user, string id)
    {
        await using var tx = await db.Database.BeginTransactionAsync();
        var flow = await RequireAsync(context, user, id, allowExpired: true);
        flow.Cancelled = true;
        flow.EmailTokenHash = null;
        flow.CreationChallengeHash = null;
        Audit(user.Id, "PasskeyAdditionCancelled", flow.Id);
        await db.SaveChangesAsync();
        await tx.CommitAsync();
    }

    private async Task<PasskeyAddition> RequireAsync(HttpContext context, ApplicationUser user,
        string? id = null, bool allowExpired = false)
    {
        var session = SessionHash(context);
        await db.Entry(user).ReloadAsync();
        RequireOwner(user);
        var flow = await db.PasskeyAdditions.FirstOrDefaultAsync(p => p.UserId == user.Id &&
            p.SessionHash == session && (id == null || p.Id == id) && !p.Cancelled && p.CompletedAt == null);
        if (flow is null || flow.Email != user.Email || flow.SecurityStamp != user.SecurityStamp)
            throw new UnauthorizedAccessException();
        if (!allowExpired && (flow.Expires <= Now ||
            flow.RegistrationUntil is long deadline && deadline <= Now))
            throw new ValidationException("追加操作が期限切れです。既存パスキーの再認証からやり直してください。");
        return flow;
    }

    private static void RequireOwner(ApplicationUser user)
    {
        if (!user.Enabled || !user.EmailConfirmed || string.IsNullOrWhiteSpace(user.Email) ||
            string.IsNullOrEmpty(user.SecurityStamp)) throw new UnauthorizedAccessException();
    }

    private static string SessionHash(HttpContext context)
    {
        var session = context.User.FindFirstValue(SessionClaim);
        if (string.IsNullOrEmpty(session))
            throw new ValidationException("追加前に一度ログアウトし、パスキーでログインし直してください。");
        return SemEndpoints.Hash(session);
    }

    private static void RequireReauthentication(PasskeyAddition flow)
    {
        if (flow.ReauthenticatedAt is null) throw new UnauthorizedAccessException();
    }

    private void RequireRegistration(PasskeyAddition flow)
    {
        RequireReauthentication(flow);
        if (flow.EmailVerifiedAt is null || flow.RegistrationUntil is null ||
            flow.RegistrationUntil <= Now) throw new ValidationException("メール再確認が未完了または期限切れです。");
    }

    private void QueueProofEmail(PasskeyAddition flow)
    {
        if (!notifications.EmailConfigured) throw new ValidationException("SMTPが設定されていません。");
        var token = SemEndpoints.Secret();
        flow.EmailTokenHash = SemEndpoints.Hash(token);
        flow.EmailVerifiedAt = null;
        flow.RegistrationUntil = null;
        flow.CreationChallengeHash = null;
        notifications.Email(db, flow.UserId, "SEM パスキー追加のメール確認",
            "プロフィール管理の「アカウント管理：パスキー」で、次の64桁の確認コードを入力してください。\n\n" +
            token + "\n\n追加操作全体の期限は既存パスキーの再認証から15分です。再送しても期限は延長されません。");
    }

    private static void CheckChallenge(string credential, string? expectedHash)
    {
        if (expectedHash is null) throw new UnauthorizedAccessException();
        try
        {
            using var json = JsonDocument.Parse(credential);
            var encoded = json.RootElement.GetProperty("response").GetProperty("clientDataJSON").GetString()!;
            encoded = encoded.Replace('-', '+').Replace('_', '/');
            encoded = encoded.PadRight((encoded.Length + 3) / 4 * 4, '=');
            using var client = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(encoded)));
            var challenge = client.RootElement.GetProperty("challenge").GetString()!;
            if (SemEndpoints.Hash(challenge) != expectedHash) throw new UnauthorizedAccessException();
        }
        catch (Exception error) when (error is JsonException or FormatException or KeyNotFoundException
            or InvalidOperationException or ArgumentNullException)
        {
            throw new ValidationException("パスキー応答の形式を確認してください。");
        }
    }

    private void Audit(string userId, string action, string flowId) =>
        db.Audits.Add(new Audit { ActorId = userId, Action = action, EntityId = flowId, At = Now });
}