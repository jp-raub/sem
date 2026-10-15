namespace Sem.Web.Runtime;

public sealed class EmailChangeOperation
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string UserId { get; set; } = "";
    public string OriginalEmailHash { get; set; } = "";
    public string? TargetEmailHash { get; set; }
    public string ProtectedTargetEmail { get; set; } = "";
    public string SecurityStamp { get; set; } = "";
    public string SessionHash { get; set; } = "";
    public string? ChallengeHash { get; set; }
    public long ChallengeExpiresAt { get; set; }
    public long? ReauthenticatedAt { get; set; }
    public string? TokenHash { get; set; }
    public long CreatedAt { get; set; }
    public long ExpiresAt { get; set; }
    public long? LastSentAt { get; set; }
    public string Status { get; set; } = "Pending";
    public long RowVersion { get; set; } = 1;
}
// Independent of operation lifetime: cancel/recreate/resend never resets these windows.
public sealed class EmailChangeRateState
{
    public string UserId { get; set; } = "";
    public string SendTimes { get; set; } = "[]";
    public string PasskeyFailures { get; set; } = "[]";
    public string VerificationFailures { get; set; } = "[]";
    public long RowVersion { get; set; } = 1;
}
public sealed class EmailChangeMail
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string UserId { get; set; } = "";
    public string OperationId { get; set; } = "";
    public string Kind { get; set; } = "Verification";
    public string ProtectedPayload { get; set; } = "";
    public long ExpiresAt { get; set; }
    public long Due { get; set; }
    public int Attempts { get; set; }
    public string Status { get; set; } = "Pending";
    public string? Claim { get; set; }
    public long? LeaseUntil { get; set; }
    public long? SentAt { get; set; }
    public string? Error { get; set; }
}
public sealed record EmailChangeMailPayload(string Recipient, string Subject, string Body);
public sealed class EmailChangeRateLimitException(long retryAfter) : Exception("試行上限です。待ち時間後に本人確認から再開してください。")
{ public long RetryAfter { get; } = Math.Max(1, retryAfter); }
