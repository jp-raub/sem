using System.Net;
namespace Sem.Authentication;

/// <summary>秘密値を診断へ含めず、公開Origin・鍵領域・信頼プロキシを明示する。</summary>
public sealed class AuthenticationSettings
{
    public string PublicOrigin { get; }
    public string RelyingPartyId { get; }
    public string KeysDirectory { get; }
    public IReadOnlyList<IPAddress> TrustedProxies { get; }
    public AuthenticationSettings(string publicOrigin, string keysDirectory, IEnumerable<IPAddress>? trustedProxies = null)
    {
        if (string.IsNullOrWhiteSpace(publicOrigin) || publicOrigin.Any(char.IsWhiteSpace) ||
            publicOrigin.Any(char.IsControl) || publicOrigin.Contains('?') || publicOrigin.Contains('#') ||
            publicOrigin.Contains('\\') || !Uri.TryCreate(publicOrigin, UriKind.Absolute, out var uri) ||
            uri.Scheme != "https" || uri.UserInfo.Length != 0 || uri.AbsolutePath != "/" ||
            uri.HostNameType is not (UriHostNameType.Dns or UriHostNameType.IPv4 or UriHostNameType.IPv6))
            throw new ArgumentException("SEM_AUTH_INVALID_ORIGIN");
        if (string.IsNullOrWhiteSpace(keysDirectory) || !Path.IsPathFullyQualified(keysDirectory))
            throw new ArgumentException("SEM_AUTH_INVALID_KEYS_DIRECTORY");
        PublicOrigin = uri.GetLeftPart(UriPartial.Authority);
        RelyingPartyId = uri.Host;
        KeysDirectory = Path.GetFullPath(keysDirectory);
        TrustedProxies = Array.AsReadOnly((trustedProxies ?? []).ToArray());
    }
}
