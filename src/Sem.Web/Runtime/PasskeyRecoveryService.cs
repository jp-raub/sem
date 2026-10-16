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

public sealed class PasskeyRecoveryService(
    ApplicationDbContext db,
    UserManager<ApplicationUser> users,
    IPasskeyRecoveryIdentity identity,
    NotificationService notifications,
    DeploymentSettings settings,
    IDataProtectionProvider protection,
    TimeProvider clock,
    IOptions<IdentityOptions> identityOptions)
{
    public const long OperationSeconds = 15 * 60;
    public const long RegistrationSeconds = 5 * 60;
    public const long AdminChallengeSeconds = 5 * 60;
    const string RatePurpose = "PasskeyRecovery";
    readonly IDataProtector mailProtector = protection.CreateProtector("SEM.PasskeyRecovery.Mail.v1");
    long Now => clock.GetUtcNow().ToUnixTimeSeconds();

    public async Task<ApplicationUser> RequireAdminAsync(HttpContext context, CancellationToken ct = default)
    {
        var principal = context.User;
        var id = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        var stamp = principal.FindFirstValue(identityOptions.Value.ClaimsIdentity.SecurityStampClaimType);
        var session = principal.FindFirstValue(PasskeyAdditionService.SessionClaim);
        if (principal.Identity?.IsAuthenticated != true || string.IsNullOrWhiteSpace(id) ||
            string.IsNullOrWhiteSpace(stamp) || string.IsNullOrWhiteSpace(session))
            throw new UnauthorizedAccessException();

        var admin = await db.Users.SingleOrDefaultAsync(x => x.Id == id, ct);
        if (admin is null) throw new UnauthorizedAccessException();
        await db.Entry(admin).ReloadAsync(ct);
        if (!admin.Enabled || !admin.EmailConfirmed || !admin.IsAdmin ||
            string.IsNullOrEmpty(admin.SecurityStamp) || admin.SecurityStamp != stamp ||
            await db.RevokedAuthSessions.AnyAsync(x => x.TokenHash == SessionHash(session), ct))
            throw new UnauthorizedAccessException();
        return admin;
    }

    public async Task<PasskeyRecoveryAdminTarget?> ReadAdminTargetAsync(
        HttpContext context, string targetUserId, CancellationToken ct = default)
    {
        _ = await RequireAdminAsync(context, ct);
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == targetUserId, ct);
        if (user is null) return null;
        var operation = await db.PasskeyRecoveryOperations.AsNoTracking()
            .Where(x => x.UserId == user.Id && x.DisabledForRecovery &&
                (x.Status == "Pending" || x.Status == "EmailConfirmed" || x.Status == "Expired") &&
                x.SecurityStamp == user.SecurityStamp &&
                x.EmailHash == EmailHash(user.Email ?? ""))
            .OrderByDescending(x => x.CreatedAt).FirstOrDefaultAsync(ct);
        var recovering = !user.Enabled && operation is not null;
        return new(user.Id, user.DisplayName, MaskEmail(user.Email), user.Enabled, recovering,
            recovering ? operation!.Id : null,
            recovering ? DateTimeOffset.FromUnixTimeSeconds(operation!.ExpiresAt) : null);
    }

    public async Task<string> CreateAdminProofOptionsAsync(
        HttpContext context, ApplicationUser admin, string targetUserId, string reason,
        CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var liveAdmin = await RequireAdminAsync(context, ct);
        if (liveAdmin.Id != admin.Id) throw new UnauthorizedAccessException();
        var safeReason = NormalizeReason(reason);
        var target = await RequireEligibleTargetAsync(targetUserId, liveAdmin.Id, ct);
        var rate = await RateAsync(target.Id, ct);
        GateFailures(rate.ProofFailures, Now);
        GateFailures(rate.EmailFailures, Now);

        var options = await identity.MakeRequestOptionsAsync(liveAdmin);
        var challengeHash = OptionsChallengeHash(options);
        var sessionHash = SessionHash(RequireSession(context));
        var contextHash = ApprovalContextHash(target.Id, safeReason);
        await db.PasskeyRecoveryAdminChallenges
            .Where(x => x.AdminId == liveAdmin.Id && x.TargetUserId == target.Id &&
                x.SessionHash == sessionHash && x.UsedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.UsedAt, Now), ct);
        db.PasskeyRecoveryAdminChallenges.Add(new()
        {
            AdminId = liveAdmin.Id, TargetUserId = target.Id, SessionHash = sessionHash,
            ContextHash = contextHash, ChallengeHash = challengeHash,
            CreatedAt = Now, ExpiresAt = Now + AdminChallengeSeconds
        });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return options;
    }

    public async Task<PasskeyRecoveryStartResult> StartAsync(
        HttpContext context, ApplicationUser admin, string targetUserId, string reason,
        string credential, bool explicitlyConfirmed, CancellationToken ct = default)
    {
        if (!explicitlyConfirmed) throw new ValidationException("認証リセットの明示確認が必要です。");
        if (!notifications.EmailConfigured) throw new ValidationException("SMTPを設定してから復旧を開始してください。");
        var safeReason = NormalizeReason(reason);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var liveAdmin = await RequireAdminAsync(context, ct);
        if (liveAdmin.Id != admin.Id) throw new UnauthorizedAccessException();
        var target = await RequireEligibleTargetAsync(targetUserId, liveAdmin.Id, ct);
        var rate = await RateAsync(target.Id, ct);
        GateFailures(rate.ProofFailures, Now);
        GateFailures(rate.EmailFailures, Now);
        GateSend(rate, Now);

        var sessionHash = SessionHash(RequireSession(context));
        var contextHash = ApprovalContextHash(target.Id, safeReason);
        var challengeHash = CredentialChallengeHash(credential);
        var proof = challengeHash is null ? null : await db.PasskeyRecoveryAdminChallenges
            .Where(x => x.AdminId == liveAdmin.Id && x.TargetUserId == target.Id &&
                x.SessionHash == sessionHash && x.ContextHash == contextHash &&
                x.ChallengeHash == challengeHash && x.UsedAt == null && x.ExpiresAt > Now)
            .OrderByDescending(x => x.CreatedAt).FirstOrDefaultAsync(ct);
        if (proof is null)
        {
            await FailProofAsync(rate, ct);
            await tx.CommitAsync(ct);
            throw new ValidationException("管理者の再認証に失敗しました。新しい確認を開始してください。");
        }
        var consumed = await db.PasskeyRecoveryAdminChallenges
            .Where(x => x.Id == proof.Id && x.UsedAt == null && x.ExpiresAt > Now)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.UsedAt, Now), ct);
        if (consumed != 1)
        {
            await FailProofAsync(rate, ct);
            await tx.CommitAsync(ct);
            throw new ValidationException("管理者の再認証は使用済みです。新しい確認を開始してください。");
        }
        if (!await identity.VerifyAndUpdateAssertionAsync(credential, liveAdmin))
        {
            await FailProofAsync(rate, ct);
            await tx.CommitAsync(ct);
            throw new ValidationException("管理者のパスキーを確認できませんでした。");
        }

        var now = Now;
        var operation = await db.PasskeyRecoveryOperations.SingleOrDefaultAsync(x => x.UserId == target.Id, ct);
        if (operation is not null && operation.Status is ("Pending" or "EmailConfirmed") &&
            operation.ExpiresAt > now)
            throw new ValidationException("このアカウントには有効な復旧処理があります。");
        var token = NewSecret();
        var tokenHash = SemEndpoints.Hash(token);
        var expiry = now + OperationSeconds;
        if (operation is null)
        {
            operation = new()
            {
                UserId = target.Id, CreatedAt = now, ExpiresAt = expiry,
                DisabledForRecovery = true
            };
            db.PasskeyRecoveryOperations.Add(operation);
        }
        operation.AdminId = liveAdmin.Id;
        operation.Reason = safeReason;
        operation.Status = "Pending";
        operation.EmailHash = EmailHash(target.Email!);
        operation.SecurityStamp = Guid.NewGuid().ToString("N");
        operation.TokenHash = tokenHash;
        operation.GrantHash = null;
        operation.GrantExpiresAt = null;
        operation.EmailVerifiedAt = null;
        operation.CreationChallengeHash = null;
        operation.CreatedAt = now;
        operation.ExpiresAt = expiry;
        operation.LastSentAt = now;
        operation.DisabledForRecovery = true;
        operation.RowVersion = checked(operation.RowVersion + 1);

        // Proof consumption, account disablement, credential deletion, revocation
        // of other credential flows, and the durable mail admission share a tx.
        target.Enabled = false;
        target.SecurityStamp = operation.SecurityStamp;
        var savedTarget = await users.UpdateAsync(target);
        if (!savedTarget.Succeeded) throw new ValidationException("対象アカウントを無効化できませんでした。");
        var oldPasskeys = await db.Set<IdentityUserPasskey<string>>()
            .Where(x => x.UserId == target.Id).ToListAsync(ct);
        db.RemoveRange(oldPasskeys);
        await InvalidateOtherCredentialFlowsAsync(target.Id, ct);
        await db.PushDevices.Where(x => x.UserId == target.Id).ExecuteDeleteAsync(ct);
        await db.PasskeyRecoveryMails.Where(x => x.OperationId == operation.Id &&
            x.Status != "Sent").ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, "Cancelled")
                .SetProperty(x => x.ProtectedPayload, "").SetProperty(x => x.Claim, (string?)null)
                .SetProperty(x => x.LeaseUntil, (long?)null), ct);
        AdmitSend(rate, now);
        QueueMail(operation, target.Email!, "RecoveryLink",
            RecoveryMailBody(operation, token), now, expiry, tokenHash);
        db.PasskeyRecoveryAudits.Add(new()
        {
            OperationId = operation.Id, AdminId = liveAdmin.Id, TargetUserId = target.Id,
            Action = "RecoveryStarted", Reason = safeReason, At = now
        });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new(operation.Id, DateTimeOffset.FromUnixTimeSeconds(expiry), MaskEmail(target.Email));
    }

    public async Task<string> CreateResendProofOptionsAsync(
        HttpContext context, ApplicationUser admin, string operationId, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var liveAdmin = await RequireAdminAsync(context, ct);
        if (liveAdmin.Id != admin.Id) throw new UnauthorizedAccessException();
        var operation = await RequirePendingOperationAsync(operationId, ct);
        var target = await db.Users.SingleAsync(x => x.Id == operation.UserId, ct);
        RequireOperationBinding(operation, target);
        var rate = await RateAsync(target.Id, ct);
        GateFailures(rate.ProofFailures, Now);
        GateFailures(rate.EmailFailures, Now);
        GateSend(rate, Now);
        var options = await identity.MakeRequestOptionsAsync(liveAdmin);
        var sessionHash = SessionHash(RequireSession(context));
        var contextHash = ResendContextHash(operation.Id, operation.UserId, operation.Reason);
        await db.PasskeyRecoveryAdminChallenges
            .Where(x => x.AdminId == liveAdmin.Id && x.TargetUserId == target.Id &&
                x.SessionHash == sessionHash && x.UsedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.UsedAt, Now), ct);
        db.PasskeyRecoveryAdminChallenges.Add(new()
        {
            AdminId = liveAdmin.Id, TargetUserId = target.Id, SessionHash = sessionHash,
            ContextHash = contextHash, ChallengeHash = OptionsChallengeHash(options),
            CreatedAt = Now, ExpiresAt = Now + AdminChallengeSeconds
        });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return options;
    }

    public async Task<PasskeyRecoveryStartResult> ResendAsync(
        HttpContext context, ApplicationUser admin, string operationId, string credential,
        CancellationToken ct = default)
    {
        if (!notifications.EmailConfigured) throw new ValidationException("SMTPを設定してから再送してください。");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var liveAdmin = await RequireAdminAsync(context, ct);
        if (liveAdmin.Id != admin.Id) throw new UnauthorizedAccessException();
        var operation = await RequirePendingOperationAsync(operationId, ct);
        var target = await db.Users.SingleAsync(x => x.Id == operation.UserId, ct);
        RequireOperationBinding(operation, target);
        var rate = await RateAsync(target.Id, ct);
        GateFailures(rate.ProofFailures, Now);
        GateFailures(rate.EmailFailures, Now);
        GateSend(rate, Now);
        var challenge = CredentialChallengeHash(credential);
        var sessionHash = SessionHash(RequireSession(context));
        var contextHash = ResendContextHash(operation.Id, operation.UserId, operation.Reason);
        var proof = challenge is null ? null : await db.PasskeyRecoveryAdminChallenges
            .Where(x => x.AdminId == liveAdmin.Id && x.TargetUserId == target.Id &&
                x.SessionHash == sessionHash && x.ContextHash == contextHash &&
                x.ChallengeHash == challenge && x.UsedAt == null && x.ExpiresAt > Now)
            .OrderByDescending(x => x.CreatedAt).FirstOrDefaultAsync(ct);
        if (proof is null)
        {
            await FailProofAsync(rate, ct);
            await tx.CommitAsync(ct);
            throw new ValidationException("管理者の再認証に失敗しました。新しい確認を開始してください。");
        }
        var consumed = await db.PasskeyRecoveryAdminChallenges
            .Where(x => x.Id == proof.Id && x.UsedAt == null && x.ExpiresAt > Now)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.UsedAt, Now), ct);
        if (consumed != 1 || !await identity.VerifyAndUpdateAssertionAsync(credential, liveAdmin))
        {
            await FailProofAsync(rate, ct);
            await tx.CommitAsync(ct);
            throw new ValidationException("管理者のパスキーを確認できませんでした。");
        }
        var token = NewSecret();
        operation.TokenHash = SemEndpoints.Hash(token);
        operation.GrantHash = null;
        operation.GrantExpiresAt = null;
        operation.EmailVerifiedAt = null;
        operation.CreationChallengeHash = null;
        operation.LastSentAt = Now;
        operation.AdminId = liveAdmin.Id;
        operation.RowVersion = checked(operation.RowVersion + 1);
        await db.PasskeyRecoveryMails.Where(x => x.OperationId == operation.Id &&
            x.Kind == "RecoveryLink" && x.Status != "Sent")
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, "Cancelled")
                .SetProperty(x => x.ProtectedPayload, "").SetProperty(x => x.Claim, (string?)null)
                .SetProperty(x => x.LeaseUntil, (long?)null), ct);
        AdmitSend(rate, Now);
        QueueMail(operation, target.Email!, "RecoveryLink",
            RecoveryMailBody(operation, token), Now, operation.ExpiresAt, operation.TokenHash);
        db.PasskeyRecoveryAudits.Add(new()
        {
            OperationId = operation.Id, AdminId = liveAdmin.Id, TargetUserId = target.Id,
            Action = "RecoveryMailResent", Reason = operation.Reason, At = Now
        });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new(operation.Id, DateTimeOffset.FromUnixTimeSeconds(operation.ExpiresAt), MaskEmail(target.Email));
    }

    public async Task<PasskeyRecoveryGrant> ConfirmMailAsync(
        string operationId, string token, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var operation = await db.PasskeyRecoveryOperations.SingleOrDefaultAsync(x => x.Id == operationId, ct);
        if (operation is null) throw new ValidationException("復旧リンクは無効または期限切れです。");
        var rate = await RateAsync(operation.UserId, ct);
        GateFailures(rate.EmailFailures, Now);
        var user = await db.Users.SingleOrDefaultAsync(x => x.Id == operation.UserId, ct);
        var valid = user is not null && operation.Status == "Pending" &&
            operation.ExpiresAt > Now && operation.DisabledForRecovery && !user.Enabled &&
            user.EmailConfirmed && !string.IsNullOrWhiteSpace(user.Email) &&
            operation.SecurityStamp == user.SecurityStamp &&
            operation.EmailHash == EmailHash(user.Email) && TokenMatches(operation.TokenHash, token);
        if (!valid)
        {
            AddFailure(rate, Now, proof: false);
            if (operation.Status == "Pending" &&
                ReadTimes(rate.EmailFailures).Count(x => x > Now - 900) >= 5)
            {
                operation.Status = "Expired";
                operation.ExpiresAt = Math.Min(operation.ExpiresAt, Now);
                operation.TokenHash = null;
                operation.GrantHash = null;
                operation.GrantExpiresAt = null;
                operation.EmailVerifiedAt = null;
                operation.CreationChallengeHash = null;
                operation.RowVersion = checked(operation.RowVersion + 1);
                await db.PasskeyRecoveryAdminChallenges
                    .Where(x => x.TargetUserId == operation.UserId)
                    .ExecuteDeleteAsync(ct);
                await db.PasskeyRecoveryMails.Where(x => x.OperationId == operation.Id &&
                    x.Kind == "RecoveryLink" && x.Status != "Sent")
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, "Cancelled")
                        .SetProperty(x => x.ProtectedPayload, "")
                        .SetProperty(x => x.ProofHash, (string?)null)
                        .SetProperty(x => x.Claim, (string?)null)
                        .SetProperty(x => x.LeaseUntil, (long?)null), ct);
            }
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            throw new ValidationException("復旧リンクは無効または期限切れです。");
        }
        var grant = NewSecret();
        var grantExpires = Math.Min(operation.ExpiresAt, Now + RegistrationSeconds);
        operation.TokenHash = null;
        operation.GrantHash = SemEndpoints.Hash(grant);
        operation.GrantExpiresAt = grantExpires;
        operation.EmailVerifiedAt = Now;
        operation.CreationChallengeHash = null;
        operation.Status = "EmailConfirmed";
        operation.RowVersion = checked(operation.RowVersion + 1);
        ClearExpired(rate, Now);
        await db.PasskeyRecoveryMails.Where(x => x.OperationId == operation.Id &&
            x.Kind == "RecoveryLink" && x.Status != "Sent")
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, "Cancelled")
                .SetProperty(x => x.ProtectedPayload, "").SetProperty(x => x.Claim, (string?)null)
                .SetProperty(x => x.LeaseUntil, (long?)null), ct);
        db.PasskeyRecoveryAudits.Add(new()
        {
            OperationId = operation.Id, AdminId = operation.AdminId, TargetUserId = operation.UserId,
            Action = "RecoveryEmailConfirmed", Reason = operation.Reason, At = Now
        });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new(operation.UserId, grant, DateTimeOffset.FromUnixTimeSeconds(grantExpires));
    }

    public async Task<string> MakePasskeyCreationOptionsAsync(
        HttpContext context, string targetUserId, string grant, CancellationToken ct = default)
    {
        _ = context; // Recovery is anonymous; the one-use email grant is its only authority.
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var (operation, target) = await RequireGrantAsync(targetUserId, grant, ct);
        var options = await identity.MakeCreationOptionsAsync(target);
        operation.CreationChallengeHash = OptionsChallengeHash(options);
        operation.RowVersion = checked(operation.RowVersion + 1);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return options;
    }

    public async Task CompletePasskeyRegistrationAsync(
        HttpContext context, string targetUserId, string grant, string credential,
        CancellationToken ct = default)
    {
        _ = context; // Do not sign in or issue a normal session after enrollment.
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var (operation, target) = await RequireGrantAsync(targetUserId, grant, ct);
        if (!ChallengeMatches(credential, operation.CreationChallengeHash))
            throw new ValidationException("新しいパスキーを確認できませんでした。");
        if (!await identity.VerifyAndSaveAttestationAsync(credential, target))
            throw new ValidationException("新しいパスキーを保存できませんでした。");

        // The grant is consumed in the same transaction as saving the native
        // credential.  Enable only after a valid credential is persisted.
        operation.Status = "Completed";
        operation.TokenHash = null;
        operation.GrantHash = null;
        operation.GrantExpiresAt = null;
        operation.CreationChallengeHash = null;
        operation.DisabledForRecovery = false;
        operation.RowVersion = checked(operation.RowVersion + 1);
        target.Enabled = true;
        target.SecurityStamp = Guid.NewGuid().ToString("N");
        var updated = await users.UpdateAsync(target);
        if (!updated.Succeeded) throw new ValidationException("復旧後のアカウントを有効化できませんでした。");
        var mail = new PasskeyRecoveryMailPayload(target.Email!,
            "SEM パスキー復旧完了",
            "アカウントのパスキーが再登録され、通常利用が再開されました。本人の操作でない場合は管理者へ連絡してください。");
        QueueProtectedMail(operation, "RecoveryComplete", mail, Now, Now + 7 * 86400, null);
        db.PasskeyRecoveryAudits.Add(new()
        {
            OperationId = operation.Id, AdminId = operation.AdminId, TargetUserId = target.Id,
            Action = "RecoveryCompleted", Reason = operation.Reason, At = Now
        });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    async Task<ApplicationUser> RequireEligibleTargetAsync(
        string targetUserId, string adminId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(targetUserId) || targetUserId == adminId)
            throw new ValidationException("対象アカウントを選択してください。");
        var target = await db.Users.SingleOrDefaultAsync(x => x.Id == targetUserId, ct);
        if (target is null || !target.EmailConfirmed || string.IsNullOrWhiteSpace(target.Email))
            throw new ValidationException("対象アカウントの登録済みメールアドレスを確認できません。");
        var previous = await db.PasskeyRecoveryOperations.AsNoTracking()
            .Where(x => x.UserId == target.Id)
            .OrderByDescending(x => x.CreatedAt).FirstOrDefaultAsync(ct);
        var provenRecoveryDisabled = !target.Enabled && previous is not null &&
            previous.DisabledForRecovery &&
            previous.SecurityStamp == target.SecurityStamp &&
            previous.EmailHash == EmailHash(target.Email) &&
            (previous.Status == "Pending" || previous.Status == "EmailConfirmed" || previous.Status == "Expired");
        if (!target.Enabled && !provenRecoveryDisabled)
            throw new ValidationException("管理者が無効化したアカウントはこの手順で復旧できません。");
        if (target.Enabled && previous is { Status: "Pending" or "EmailConfirmed" } &&
            previous.ExpiresAt > Now)
            throw new ValidationException("このアカウントには有効な復旧処理があります。");
        return target;
    }

    async Task<PasskeyRecoveryOperation> RequirePendingOperationAsync(string operationId, CancellationToken ct)
    {
        var operation = await db.PasskeyRecoveryOperations.SingleOrDefaultAsync(x => x.Id == operationId, ct);
        if (operation is null || operation.Status != "Pending" ||
            operation.ExpiresAt <= Now || !operation.DisabledForRecovery)
            throw new ValidationException("復旧処理は無効または期限切れです。");
        return operation;
    }

    void RequireOperationBinding(PasskeyRecoveryOperation operation, ApplicationUser target)
    {
        if (target.Enabled || !target.EmailConfirmed || string.IsNullOrWhiteSpace(target.Email) ||
            !operation.DisabledForRecovery || operation.SecurityStamp != target.SecurityStamp ||
            operation.EmailHash != EmailHash(target.Email))
            throw new UnauthorizedAccessException();
    }

    async Task<(PasskeyRecoveryOperation Operation, ApplicationUser Target)> RequireGrantAsync(
        string targetUserId, string grant, CancellationToken ct)
    {
        var grantHash = string.IsNullOrWhiteSpace(grant) ? "" : SemEndpoints.Hash(grant);
        var operation = await db.PasskeyRecoveryOperations.SingleOrDefaultAsync(x =>
            x.UserId == targetUserId && x.Status == "EmailConfirmed" &&
            x.GrantHash == grantHash && x.GrantExpiresAt > Now &&
            x.ExpiresAt > Now && x.DisabledForRecovery, ct);
        var target = operation is null ? null :
            await db.Users.SingleOrDefaultAsync(x => x.Id == targetUserId, ct);
        if (operation is null || target is null || target.Enabled || !target.EmailConfirmed ||
            string.IsNullOrWhiteSpace(target.Email) || operation.SecurityStamp != target.SecurityStamp ||
            operation.EmailHash != EmailHash(target.Email))
            throw new UnauthorizedAccessException();
        return (operation, target);
    }

    async Task<PasskeyRecoveryRateState> RateAsync(string userId, CancellationToken ct)
    {
        var state = await db.PasskeyRecoveryRateStates.SingleOrDefaultAsync(
            x => x.UserId == userId && x.Purpose == RatePurpose, ct);
        if (state is not null) return state;
        state = new() { UserId = userId, Purpose = RatePurpose };
        db.PasskeyRecoveryRateStates.Add(state);
        return state;
    }

    async Task FailProofAsync(PasskeyRecoveryRateState state, CancellationToken ct)
    {
        AddFailure(state, Now, proof: true);
        await db.SaveChangesAsync(ct);
    }

    void GateSend(PasskeyRecoveryRateState state, long now)
    {
        var times = ReadTimes(state.SendTimes).Where(x => x > now - 3600).Order().ToArray();
        if (times.Length >= 3)
            throw new PasskeyRecoveryRateLimitException(times[0] + 3600 - now);
        if (times.Length > 0 && times[^1] + 60 > now)
            throw new PasskeyRecoveryRateLimitException(times[^1] + 60 - now);
    }

    void AdmitSend(PasskeyRecoveryRateState state, long now)
    {
        state.SendTimes = JsonSerializer.Serialize(ReadTimes(state.SendTimes)
            .Where(x => x > now - 3600).Append(now).Order().ToArray());
        state.RowVersion = checked(state.RowVersion + 1);
    }

    static void GateFailures(string serialized, long now)
    {
        var times = ReadTimes(serialized).Where(x => x > now - 900).Order().ToArray();
        if (times.Length >= 5)
            throw new PasskeyRecoveryRateLimitException(times[0] + 900 - now);
    }

    static void AddFailure(string serialized, long now, Action<string> set)
    {
        set(JsonSerializer.Serialize(ReadTimes(serialized).Where(x => x > now - 900).Append(now).Order().ToArray()));
    }

    static void AddFailure(PasskeyRecoveryRateState state, long now, bool proof)
    {
        if (proof) AddFailure(state.ProofFailures, now, v => state.ProofFailures = v);
        else AddFailure(state.EmailFailures, now, v => state.EmailFailures = v);
        state.RowVersion = checked(state.RowVersion + 1);
    }

    static void ClearExpired(PasskeyRecoveryRateState state, long now)
    {
        state.EmailFailures = JsonSerializer.Serialize(ReadTimes(state.EmailFailures)
            .Where(x => x > now - 900).Order().ToArray());
        state.RowVersion = checked(state.RowVersion + 1);
    }

    void QueueMail(PasskeyRecoveryOperation operation, string recipient, string kind,
        string body, long now, long expiry, string? tokenHash)
    {
        QueueProtectedMail(operation, kind,
            new PasskeyRecoveryMailPayload(recipient, "SEM パスキー復旧のご案内", body),
            now, expiry, tokenHash);
    }

    void QueueProtectedMail(PasskeyRecoveryOperation operation, string kind,
        PasskeyRecoveryMailPayload payload, long now, long expiry, string? tokenHash)
    {
        db.PasskeyRecoveryMails.Add(new()
        {
            UserId = operation.UserId, OperationId = operation.Id, Kind = kind,
            ProtectedPayload = mailProtector.Protect(JsonSerializer.Serialize(payload)),
            Due = now, ExpiresAt = expiry, ProofHash = tokenHash
        });
    }

    string RecoveryMailBody(PasskeyRecoveryOperation operation, string token)
    {
        var link = settings.PublicUrl.TrimEnd('/') + "/recovery#op=" +
            Uri.EscapeDataString(operation.Id) + "&token=" + Uri.EscapeDataString(token);
        return $"管理者の承認により、パスキー復旧が開始されました。\n" +
            $"理由: {operation.Reason}\nこの操作は15分で期限切れになります。\n" +
            $"登録済みの新しいパスキーだけを設定してください。\n{link}\n" +
            "本人が依頼していない場合は、リンクを開かず管理者へ連絡してください。";
    }

    static string NormalizeReason(string reason)
    {
        var value = (reason ?? "").Trim();
        if (value.Length is < 3 or > 500 || value.Any(char.IsControl))
            throw new ValidationException("理由は3〜500文字で入力してください。制御文字は使用できません。");
        return value;
    }

    string EmailHash(string email) => SemEndpoints.Hash(users.NormalizeEmail(email) ?? "");

    static string MaskEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email) || !email.Contains('@')) return "";
        var parts = email.Split('@', 2);
        var local = parts[0];
        var prefix = local.Length <= 1 ? local : local[..Math.Min(2, local.Length)];
        return prefix + new string('•', Math.Clamp(local.Length - prefix.Length, 1, 8)) + "@" + parts[1];
    }

    static string NewSecret() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    static string RequireSession(HttpContext context) =>
        context.User.FindFirstValue(PasskeyAdditionService.SessionClaim) ??
        throw new UnauthorizedAccessException();

    static string SessionHash(string session) => SemEndpoints.Hash(session);

    static string ApprovalContextHash(string targetId, string reason) =>
        SemEndpoints.Hash("start\n" + targetId + "\n" + reason);

    static string ResendContextHash(string operationId, string targetId, string reason) =>
        SemEndpoints.Hash("resend\n" + operationId + "\n" + targetId + "\n" + reason);

    static string OptionsChallengeHash(string options)
    {
        using var json = JsonDocument.Parse(options);
        return SemEndpoints.Hash(json.RootElement.GetProperty("challenge").GetString()!);
    }

    static string? CredentialChallengeHash(string credential)
    {
        try
        {
            using var json = JsonDocument.Parse(credential);
            var encoded = json.RootElement.GetProperty("response").GetProperty("clientDataJSON")
                .GetString()!.Replace('-', '+').Replace('_', '/');
            var data = Convert.FromBase64String(encoded.PadRight((encoded.Length + 3) / 4 * 4, '='));
            using var clientData = JsonDocument.Parse(data);
            return SemEndpoints.Hash(clientData.RootElement.GetProperty("challenge").GetString()!);
        }
        catch (Exception e) when (e is JsonException or FormatException or KeyNotFoundException or
            InvalidOperationException or ArgumentException or NullReferenceException)
        {
            return null;
        }
    }

    static bool ChallengeMatches(string credential, string? expected) =>
        expected is not null && CredentialChallengeHash(credential) is { } candidate &&
        FixedEquals(expected, candidate);

    static bool TokenMatches(string? expectedHash, string token) =>
        expectedHash is not null && !string.IsNullOrWhiteSpace(token) &&
        FixedEquals(expectedHash, SemEndpoints.Hash(token));

    static bool FixedEquals(string left, string right) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(left), Encoding.UTF8.GetBytes(right));

    static long[] ReadTimes(string? value)
    {
        try { return string.IsNullOrWhiteSpace(value) ? [] : JsonSerializer.Deserialize<long[]>(value) ?? []; }
        catch (JsonException) { return []; }
    }

    async Task InvalidateOtherCredentialFlowsAsync(string userId, CancellationToken ct)
    {
        await db.EmailTickets.Where(x => x.UserId == userId && !x.Used)
            .ExecuteDeleteAsync(ct);
        await db.PasskeyAdditions.Where(x => x.UserId == userId && !x.Cancelled && x.CompletedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Cancelled, true)
                .SetProperty(x => x.EmailTokenHash, (string?)null)
                .SetProperty(x => x.CreationChallengeHash, (string?)null)
                .SetProperty(x => x.ReauthenticationChallengeHash, (string?)null), ct);
        await db.EmailChangeOperations.Where(x => x.UserId == userId && x.Status == "Pending")
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, "Invalidated")
                .SetProperty(x => x.TokenHash, (string?)null)
                .SetProperty(x => x.ChallengeHash, (string?)null)
                .SetProperty(x => x.TargetEmailHash, (string?)null)
                .SetProperty(x => x.ProtectedTargetEmail, "")
                .SetProperty(x => x.RowVersion, x => x.RowVersion + 1), ct);
        await db.EmailChangeMails.Where(x => x.UserId == userId && x.Status != "Sent")
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, "Cancelled")
                .SetProperty(x => x.ProtectedPayload, "").SetProperty(x => x.Claim, (string?)null)
                .SetProperty(x => x.LeaseUntil, (long?)null), ct);
    }
}
