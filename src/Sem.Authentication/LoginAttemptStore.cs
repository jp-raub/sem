using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
namespace Sem.Authentication;

/// <summary>標準Identityのchallengeへ一回限りのサーバー側消費を追加。署名検証は行わない。</summary>
public sealed class LoginAttemptStore(TimeProvider clock)
{
    public const string CookieName = "__Host-SEM-LoginAttempt";
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);
    const int Capacity = 4096;
    readonly object gate = new();
    readonly Dictionary<string, (string ChallengeHash, DateTimeOffset Until)> pending = [];
    public string? Issue(string challenge)
    {
        lock (gate) {
            var now = clock.GetUtcNow();
            foreach (var key in pending.Where(e => e.Value.Until <= now).Select(e => e.Key).ToArray()) pending.Remove(key);
            if (pending.Count >= Capacity) return null;
            var token = AuthenticationSession.NewSession();
            pending.Add(AuthenticationSession.Hash(token), (AuthenticationSession.Hash(challenge), now + Lifetime));
            return token;
        }
    }
    public bool Consume(string? token, string challenge)
    {
        if (token is null || token.Length != 64) return false;
        lock (gate) {
            return pending.Remove(AuthenticationSession.Hash(token), out var attempt) &&
                attempt.Until > clock.GetUtcNow() && attempt.ChallengeHash == AuthenticationSession.Hash(challenge);
        }
    }
    public static string ReadChallenge(string credential)
    {
        using var outer = JsonDocument.Parse(credential, new JsonDocumentOptions {MaxDepth=8});
        var encoded = outer.RootElement.GetProperty("response").GetProperty("clientDataJSON").GetString()!;
        using var client = JsonDocument.Parse(WebEncoders.Base64UrlDecode(encoded), new JsonDocumentOptions {MaxDepth=4});
        return client.RootElement.GetProperty("challenge").GetString() ?? throw new JsonException();
    }
    public static CookieOptions CookieOptions() => new() {
        Secure=true, HttpOnly=true, SameSite=SameSiteMode.Strict, Path="/", MaxAge=Lifetime, IsEssential=true
    };
}
