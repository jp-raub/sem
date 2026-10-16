using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Sem.Web.Core;
using Sem.Web.Data;

namespace Sem.Web.Runtime;

public sealed record DeviceNotificationActor(ApplicationUser User, string SessionHash);
public sealed record DeviceNotificationInfo(string ActorId, bool PushConfigured, string? PushPublicKey);
public sealed record DeviceNotificationResult(string ActorId, bool Registered);

/// <summary>Manages the current browser's push binding; it never sends a push.</summary>
public sealed class DeviceNotificationService(
    ApplicationDbContext db,
    NotificationService notifications,
    IOptions<IdentityOptions> identityOptions)
{
    public async Task<DeviceNotificationActor> RequireActorAsync(HttpContext context, CancellationToken ct = default)
    {
        var principal = context.User;
        if (!await LiveAuthenticationSession.ValidAsync(db, principal, identityOptions.Value, ct))
            throw new UnauthorizedAccessException();

        var userId = principal.FindFirstValue(identityOptions.Value.ClaimsIdentity.UserIdClaimType);
        var session = principal.FindFirstValue(PasskeyAdditionService.SessionClaim);
        if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(session))
            throw new UnauthorizedAccessException();
        var user = await db.Users.SingleOrDefaultAsync(x => x.Id == userId, ct);
        if (user is not { Enabled: true, EmailConfirmed: true } ||
            user.SecurityStamp != principal.FindFirstValue(identityOptions.Value.ClaimsIdentity.SecurityStampClaimType))
            throw new UnauthorizedAccessException();
        return new DeviceNotificationActor(user, SemEndpoints.Hash(session));
    }

    // Endpoint entrypoints call this overload so actor validation uses the live request principal.
    public async Task<DeviceNotificationInfo> GetInfoAsync(HttpContext context, CancellationToken ct = default)
    {
        var actor = await RequireActorAsync(context, ct);
        return new DeviceNotificationInfo(actor.User.Id, notifications.PushConfigured,
            notifications.PushConfigured ? notifications.PublicKey : null);
    }

    public async Task<DeviceNotificationResult> StatusAsync(HttpContext context, string endpoint, CancellationToken ct = default)
    {
        var actor = await RequireActorAsync(context, ct);
        ValidateEndpoint(endpoint);
        var device = await db.PushDevices.AsNoTracking().SingleOrDefaultAsync(
            x => x.UserId == actor.User.Id && x.Endpoint == endpoint, ct);
        var registered = device is not null && device.SecurityStamp == actor.User.SecurityStamp &&
            device.SessionHash == actor.SessionHash && await PushBinding.Valid(db, actor.User, device, ct);
        return new DeviceNotificationResult(actor.User.Id, registered);
    }

    public async Task<DeviceNotificationResult> RegisterAsync(HttpContext context, string endpoint,
        string p256dh, string auth, CancellationToken ct = default)
    {
        var actor = await RequireActorAsync(context, ct);
        if (!notifications.PushConfigured) throw new ValidationException("Push配信鍵が未設定です。");
        ValidateEndpoint(endpoint);
        ValidateKey(p256dh, 65, requireUncompressedPoint: true, "p256dh");
        ValidateKey(auth, 16, requireUncompressedPoint: false, "auth");

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var device = await db.PushDevices.SingleOrDefaultAsync(x => x.Endpoint == endpoint, ct);
        var bindingChanges = device is not null && (device.UserId != actor.User.Id ||
            device.SecurityStamp != actor.User.SecurityStamp || device.SessionHash != actor.SessionHash);
        if (bindingChanges)
        {
            var p256dhMatches = FixedTimeMatches(device!.P256dh, p256dh);
            var authMatches = FixedTimeMatches(device!.Auth, auth);
            if (!(p256dhMatches & authMatches))
                throw new ValidationException("既存のPush購読をこのセッションへ再紐付けできません。");
        }
        if (device is not null && device.UserId != actor.User.Id)
        {
            db.PushDevices.Remove(device);
            await db.SaveChangesAsync(ct);
            device = null;
        }
        if (device is null)
        {
            device = new PushDevice { UserId = actor.User.Id, Endpoint = endpoint };
            db.PushDevices.Add(device);
        }
        device.P256dh = p256dh;
        device.Auth = auth;
        device.SecurityStamp = actor.User.SecurityStamp;
        device.SessionHash = actor.SessionHash;
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new DeviceNotificationResult(actor.User.Id, true);
    }

    public async Task<DeviceNotificationResult> UnregisterAsync(HttpContext context, string endpoint,
        CancellationToken ct = default)
    {
        var actor = await RequireActorAsync(context, ct);
        ValidateEndpoint(endpoint);
        await db.PushDevices.Where(x => x.UserId == actor.User.Id && x.Endpoint == endpoint &&
            x.SecurityStamp == actor.User.SecurityStamp && x.SessionHash == actor.SessionHash).ExecuteDeleteAsync(ct);
        return new DeviceNotificationResult(actor.User.Id, false);
    }

    private static void ValidateEndpoint(string endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint) || endpoint.Length > 2048 || endpoint.Any(char.IsControl) ||
            !PushBinding.AllowedEndpoint(endpoint))
            throw new ValidationException("許可されていないPush接続先です。");
    }

    private static void ValidateKey(string value, int expectedLength, bool requireUncompressedPoint, string name)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 128 || value.Any(c =>
                !(c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_')) ||
            value.Length % 4 == 1)
            throw new ValidationException($"{name}の形式を確認してください。");
        try
        {
            var base64 = value.Replace('-', '+').Replace('_', '/');
            base64 += new string('=', (4 - base64.Length % 4) % 4);
            var bytes = Convert.FromBase64String(base64);
            var canonical = Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            if (bytes.Length != expectedLength || canonical != value ||
                requireUncompressedPoint && bytes[0] != 0x04)
                throw new ValidationException($"{name}の形式を確認してください。");
            if (requireUncompressedPoint) ValidateP256Point(bytes, name);
        }
        catch (FormatException)
        {
            throw new ValidationException($"{name}の形式を確認してください。");
        }
    }

    private static void ValidateP256Point(byte[] encoded, string name)
    {
        try
        {
            using var peer = ECDiffieHellman.Create();
            peer.ImportParameters(new ECParameters
            {
                Curve = ECCurve.NamedCurves.nistP256,
                Q = new ECPoint { X = encoded[1..33], Y = encoded[33..65] }
            });
            using var probe = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            _ = probe.DeriveKeyMaterial(peer.PublicKey);
        }
        catch (Exception e) when (e is CryptographicException or ArgumentException)
        {
            throw new ValidationException($"{name}は有効なP-256公開鍵ではありません。");
        }
    }

    private static bool FixedTimeMatches(string? stored, string supplied)
    {
        var storedHash = SHA256.HashData(Encoding.UTF8.GetBytes(stored ?? ""));
        var suppliedHash = SHA256.HashData(Encoding.UTF8.GetBytes(supplied));
        return CryptographicOperations.FixedTimeEquals(storedHash, suppliedHash);
    }
}
