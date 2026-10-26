// 保全済み旧mainからモデル宣言のみを抽出。サービス・画面・起動処理は含めない。

namespace Sem.Web.Runtime;

/// <summary>
/// A reset can only begin from an enabled, confirmed account or continue from a
/// record which this recovery flow itself disabled.  The record is deliberately
/// durable so that an expired recovery can be restarted without making an
/// administrator-disabled account recoverable.
/// </summary>
public sealed class PasskeyRecoveryOperation
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string UserId { get; set; } = "";
    public string? AdminId { get; set; }
    public string RecoveryMode { get; set; } = "Administrator";
    public string ActorType { get; set; } = "Administrator";
    public string ActorId { get; set; } = "";
    public string? BackupId { get; set; }
    public string Reason { get; set; } = "";
    public string Status { get; set; } = "Pending";
    public string EmailHash { get; set; } = "";
    public string SecurityStamp { get; set; } = "";
    public string? TokenHash { get; set; }
    public string? GrantHash { get; set; }
    public string? CreationChallengeHash { get; set; }
    public long CreatedAt { get; set; }
    public long ExpiresAt { get; set; }
    public long? EmailVerifiedAt { get; set; }
    public long? GrantExpiresAt { get; set; }
    public long? LastSentAt { get; set; }
    public bool DisabledForRecovery { get; set; }
    public long RowVersion { get; set; } = 1;
}


public sealed class PasskeyRecoveryAdminChallenge
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string AdminId { get; set; } = "";
    public string TargetUserId { get; set; } = "";
    public string SessionHash { get; set; } = "";
    public string ContextHash { get; set; } = "";
    public string ChallengeHash { get; set; } = "";
    public long CreatedAt { get; set; }
    public long ExpiresAt { get; set; }
    public long? UsedAt { get; set; }
}


public sealed class PasskeyRecoveryRateState
{
    public string UserId { get; set; } = "";
    public string Purpose { get; set; } = "Recovery";
    public string SendTimes { get; set; } = "[]";
    public string ProofFailures { get; set; } = "[]";
    public string EmailFailures { get; set; } = "[]";
    public long RowVersion { get; set; } = 1;
}


public sealed class PasskeyRecoveryMail
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string UserId { get; set; } = "";
    public string OperationId { get; set; } = "";
    public string Kind { get; set; } = "RecoveryLink";
    public string ProtectedPayload { get; set; } = "";
    public string? ProofHash { get; set; }
    public long Due { get; set; }
    public long ExpiresAt { get; set; }
    public string Status { get; set; } = "Pending";
    public int Attempts { get; set; }
    public long? SentAt { get; set; }
    public string? Claim { get; set; }
    public long? LeaseUntil { get; set; }
    public string? Error { get; set; }
}


public sealed class PasskeyRecoveryAudit
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string OperationId { get; set; } = "";
    public string? AdminId { get; set; }
    public string ActorType { get; set; } = "Administrator";
    public string ActorId { get; set; } = "";
    public string? BackupId { get; set; }
    public string TargetUserId { get; set; } = "";
    public string Action { get; set; } = "";
    public string Reason { get; set; } = "";
    public long At { get; set; }
}
