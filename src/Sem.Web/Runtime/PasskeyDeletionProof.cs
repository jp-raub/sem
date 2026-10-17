namespace Sem.Web.Runtime;

/// <summary>
/// A purpose-specific, one-use proof for deleting one passkey. Credential
/// identifiers are stored only as a one-way digest; the assertion itself and
/// its client data are never persisted.
/// </summary>
public sealed class PasskeyDeletionProof
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string UserId { get; set; } = "";
    public string CredentialHash { get; set; } = "";
    public string SessionHash { get; set; } = "";
    public string SecurityStamp { get; set; } = "";
    public string? ChallengeHash { get; set; }
    public long CreatedAt { get; set; }
    public long ExpiresAt { get; set; }
    public long? VerifiedAt { get; set; }
    public long? ConsumedAt { get; set; }
}