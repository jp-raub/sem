using System.Buffers.Text;
using System.ComponentModel.DataAnnotations;
using System.Data;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Sem.Web.Data;

namespace Sem.Web.Runtime;

public sealed class AccountPasskeyService(
    ApplicationDbContext db,
    UserManager<ApplicationUser> users,
    SignInManager<ApplicationUser> signIn,
    PasskeyAdditionService additions,
    TimeProvider clock,
    IOptions<IdentityOptions> identityOptions)
{
    public const long DeleteProofSeconds = 5 * 60;
    long Now => clock.GetUtcNow().ToUnixTimeSeconds();

    public async Task<object> ListAsync(HttpContext context, string? expectedActor, CancellationToken ct)
    {
        var user = await ActorAsync(context.User, expectedActor, ct);
        var keys = await users.GetPasskeysAsync(user);
        return new
        {
            actorId = user.Id,
            email = user.Email,
            emailConfirmed = user.EmailConfirmed,
            passkeys = keys.Select(key => new
            {
                id = Encode(key.CredentialId),
                name = key.Name,
                createdAt = key.CreatedAt
            }),
            addition = await additions.StatusAsync(context, user)
        };
    }

    public async Task RenameAsync(ClaimsPrincipal principal, string expectedActor, string credentialId,
        string name, CancellationToken ct)
    {
        name = name.Trim();
        if (name.Length is < 1 or > 200 || name.Any(char.IsControl))
            throw new ValidationException("パスキー名は1〜200文字で入力してください。");
        var bytes = Decode(credentialId);
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var user = await ActorAsync(principal, expectedActor, ct);
        var key = await users.GetPasskeyAsync(user, bytes);
        if (key is null) throw new AccountPasskeyConflictException("対象のパスキーが見つかりません。最新状態を再取得してください。");
        key.Name = name;
        var saved = await users.AddOrUpdatePasskeyAsync(user, key);
        if (!saved.Succeeded) throw new ValidationException("パスキー名を更新できませんでした。");
        db.Audits.Add(new Audit { ActorId = user.Id, Action = "PasskeyRenamed", EntityId = SemEndpoints.Hash(credentialId), At = Now });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    public async Task<object> BeginDeleteAsync(HttpContext context, string expectedActor,
        string credentialId, CancellationToken ct)
    {
        var bytes = Decode(credentialId);
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var user = await ActorAsync(context.User, expectedActor, ct);
        if (await users.GetPasskeyAsync(user, bytes) is null)
            throw new AccountPasskeyConflictException("対象のパスキーが見つかりません。最新状態を再取得してください。");

        var options = JsonNode.Parse(await signIn.MakePasskeyRequestOptionsAsync(user))!.AsObject();
        var allow = options["allowCredentials"] as JsonArray;
        var targetId = Encode(bytes);
        var target = allow?.FirstOrDefault(entry =>
            string.Equals(entry?["id"]?.GetValue<string>(), targetId, StringComparison.Ordinal));
        if (target is null) throw new ValidationException("対象のパスキーで再認証できません。");
        options["allowCredentials"] = new JsonArray(target.DeepClone());

        var sessionHash = SessionHash(context.User);
        await db.PasskeyDeletionProofs.Where(x => x.UserId == user.Id &&
                x.SessionHash == sessionHash && x.ConsumedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.ConsumedAt, Now)
                .SetProperty(x => x.ChallengeHash, (string?)null), ct);
        var proof = new PasskeyDeletionProof
        {
            UserId = user.Id,
            CredentialHash = SemEndpoints.Hash(credentialId),
            SessionHash = sessionHash,
            SecurityStamp = user.SecurityStamp!,
            ChallengeHash = SemEndpoints.Hash(options["challenge"]!.GetValue<string>()),
            CreatedAt = Now,
            ExpiresAt = Now + DeleteProofSeconds
        };
        db.PasskeyDeletionProofs.Add(proof);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new { proofId = proof.Id, expiresAt = proof.ExpiresAt, publicKey = options };
    }

    public async Task VerifyDeleteAsync(HttpContext context, string expectedActor, string proofId,
        string credential, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var user = await ActorAsync(context.User, expectedActor, ct);
        var proof = await RequireProofAsync(context.User, user, proofId, ct, requireVerified: false);
        if (proof.ChallengeHash is null || proof.ExpiresAt <= Now ||
            !ChallengeMatches(credential, proof.ChallengeHash))
            throw new ValidationException("対象パスキーの再認証が期限切れか無効です。");

        var assertion = await signIn.PerformPasskeyAssertionAsync(credential);
        if (!assertion.Succeeded || assertion.User.Id != user.Id ||
            SemEndpoints.Hash(Encode(assertion.Passkey.CredentialId)) != proof.CredentialHash)
            throw new ValidationException("指定した既存パスキーで再認証できませんでした。");
        var saved = await users.AddOrUpdatePasskeyAsync(user, assertion.Passkey);
        if (!saved.Succeeded) throw new ValidationException("再認証結果を更新できませんでした。");

        user = await ActorAsync(context.User, expectedActor, ct);
        proof.ChallengeHash = null;
        proof.VerifiedAt = Now;
        proof.ExpiresAt = Now + DeleteProofSeconds;
        db.Audits.Add(new Audit { ActorId = user.Id, Action = "PasskeyDeleteProofIssued", EntityId = proof.Id, At = Now });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    public async Task<bool> ConfirmDeleteAsync(HttpContext context, string expectedActor, string proofId,
        bool confirmed, CancellationToken ct)
    {
        if (!confirmed) throw new ValidationException("パスキー削除の明示確認が必要です。");
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var user = await ActorAsync(context.User, expectedActor, ct);
        var proof = await RequireProofAsync(context.User, user, proofId, ct, requireVerified: true);
        proof.ConsumedAt = Now;

        // This count is read while holding the same serializable SQLite write
        // transaction used by add-completion and recovery credential changes.
        var keys = await users.GetPasskeysAsync(user);
        if (keys.Count <= 1)
        {
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return false;
        }
        var target = keys.SingleOrDefault(k => SemEndpoints.Hash(Encode(k.CredentialId)) == proof.CredentialHash);
        if (target is null)
        {
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            throw new AccountPasskeyConflictException("対象のパスキーはすでに変更されています。最新状態を再取得してください。");
        }
        var removed = await users.RemovePasskeyAsync(user, target.CredentialId);
        if (!removed.Succeeded) throw new AccountPasskeyConflictException("パスキーを削除できませんでした。最新状態を再取得してください。");
        db.Audits.Add(new Audit { ActorId = user.Id, Action = "PasskeyDeleted", EntityId = proof.Id, At = Now });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return true;
    }

    public async Task CancelDeleteAsync(HttpContext context, string expectedActor, string proofId, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var user = await ActorAsync(context.User, expectedActor, ct);
        // The user may cancel either before native verification (while the
        // challenge is live) or after it (while the final confirmation is
        // pending). Both states remain bound to the same actor/session/key.
        var proof = await RequireProofAsync(context.User, user, proofId, ct, requireVerified: null);
        var keys = await users.GetPasskeysAsync(user);
        if (!keys.Any(key => SemEndpoints.Hash(Encode(key.CredentialId)) == proof.CredentialHash))
            throw new AccountPasskeyConflictException("対象のパスキーはすでに変更されています。最新状態を再取得してください。");
        proof.ConsumedAt = Now;
        proof.ChallengeHash = null;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    public async Task<ApplicationUser> ActorAsync(ClaimsPrincipal principal, string? expectedActor,
        CancellationToken ct = default)
    {
        var id = principal.FindFirstValue(identityOptions.Value.ClaimsIdentity.UserIdClaimType);
        var stamp = principal.FindFirstValue(identityOptions.Value.ClaimsIdentity.SecurityStampClaimType);
        var session = principal.FindFirstValue(PasskeyAdditionService.SessionClaim);
        var user = id is null ? null : await users.FindByIdAsync(id);
        if (user is not null) await db.Entry(user).ReloadAsync(ct);
        if (principal.Identity?.IsAuthenticated != true || user is null || !user.Enabled ||
            !user.EmailConfirmed || string.IsNullOrWhiteSpace(user.Email) ||
            string.IsNullOrEmpty(user.SecurityStamp) || stamp != user.SecurityStamp ||
            string.IsNullOrEmpty(session) || expectedActor is not null && expectedActor != user.Id ||
            await db.RevokedAuthSessions.AnyAsync(x => x.TokenHash == SemEndpoints.Hash(session), ct))
            throw new UnauthorizedAccessException();
        return user;
    }

    async Task<PasskeyDeletionProof> RequireProofAsync(ClaimsPrincipal principal, ApplicationUser user,
        string proofId, CancellationToken ct, bool? requireVerified)
    {
        if (!Guid.TryParseExact(proofId, "N", out _)) throw new ValidationException("削除確認をやり直してください。");
        var proof = await db.PasskeyDeletionProofs.SingleOrDefaultAsync(x =>
            x.Id == proofId && x.UserId == user.Id && x.SessionHash == SessionHash(principal) &&
            x.SecurityStamp == user.SecurityStamp && x.ConsumedAt == null && x.ExpiresAt > Now &&
            (requireVerified == true && x.VerifiedAt != null && x.ChallengeHash == null ||
             requireVerified == false && x.VerifiedAt == null && x.ChallengeHash != null ||
             requireVerified == null &&
                ((x.VerifiedAt == null && x.ChallengeHash != null) ||
                 (x.VerifiedAt != null && x.ChallengeHash == null))),
            ct);
        return proof ?? throw new AccountPasskeyConflictException("削除確認が失効しました。対象のパスキーからやり直してください。");
    }

    static string SessionHash(ClaimsPrincipal principal)
    {
        var session = principal.FindFirstValue(PasskeyAdditionService.SessionClaim);
        return string.IsNullOrEmpty(session) ? throw new UnauthorizedAccessException() : SemEndpoints.Hash(session);
    }

    static bool ChallengeMatches(string credential, string expectedHash)
    {
        try
        {
            using var json = JsonDocument.Parse(credential, new JsonDocumentOptions { MaxDepth = 12 });
            var encoded = json.RootElement.GetProperty("response").GetProperty("clientDataJSON").GetString()!;
            encoded = encoded.Replace('-', '+').Replace('_', '/').PadRight((encoded.Length + 3) / 4 * 4, '=');
            using var clientData = JsonDocument.Parse(new UTF8Encoding(false, true).GetString(Convert.FromBase64String(encoded)));
            var challenge = clientData.RootElement.GetProperty("challenge").GetString();
            return challenge is not null && SemEndpoints.Hash(challenge) == expectedHash;
        }
        catch (Exception ex) when (ex is JsonException or FormatException or KeyNotFoundException or
                                   InvalidOperationException or ArgumentNullException or DecoderFallbackException)
        {
            return false;
        }
    }

    static byte[] Decode(string credentialId)
    {
        if (credentialId.Length is < 1 or > 2048 ||
            credentialId.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_')))
            throw new ValidationException("パスキーを確認してください。");
        try
        {
            var bytes = Base64Url.DecodeFromChars(credentialId);
            if (bytes.Length == 0 || Encode(bytes) != credentialId)
                throw new ValidationException("パスキーを確認してください。");
            return bytes;
        }
        catch (FormatException) { throw new ValidationException("パスキーを確認してください。"); }
    }

    static string Encode(byte[] value) => Base64Url.EncodeToString(value);
}

public sealed class AccountPasskeyConflictException(string message) : Exception(message);
