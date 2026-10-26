// 保全済み旧mainからモデル宣言のみを抽出。サービス・画面・起動処理は含めない。
using Microsoft.EntityFrameworkCore;
using Sem.Web.Data;
namespace Sem.Web.Core;

// Program registers pipeline DI/runtime/worker through the composition extension
// and maps the Core HTTP endpoints separately.
// Missing durable activation state is OFF. Every audited activation transition MUST use a fresh,
// monotonically increasing Epoch, including when re-enabling after a disable.
public sealed class ManualNotificationPushActivation
{
    public int Id { get; set; } = 1;
    public bool Enabled { get; set; }
    public long Epoch { get; set; } = 1;
}

// Created ONLY in the original acceptance transaction, never during replay/backfill.
// No timestamp cutoff: even older accepts within the same second stay excluded.
public sealed class ManualNotificationPushAdmission
{
    public string OperationId { get; set; } = "";
    public long Epoch { get; set; }
    public long AcceptedAt { get; set; }
    public long ExpiresAt { get; set; }
}
