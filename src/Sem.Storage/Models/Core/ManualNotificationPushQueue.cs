// 保全済み旧mainからモデル宣言のみを抽出。サービス・画面・起動処理は含めない。
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Sem.Web.Data;
using Sem.Web.Runtime;
namespace Sem.Web.Core;

// Preparations pin the accepted audience's device set once; later devices never expand that snapshot.
public sealed class ManualNotificationPushPreparation
{
    public string RecipientId { get; set; } = "";
    public string Outcome { get; set; } = "";
    public long PreparedAt { get; set; }
    public int DeviceCount { get; set; }
}

public sealed class ManualNotificationPushDelivery
{
    public string Id { get; set; } = "";
    public string RecipientId { get; set; } = "";
    public string DeviceId { get; set; } = "";
    // A digest only: never copy endpoint, subscription keys, stamp or session to the queue.
    public string BindingFingerprint { get; set; } = "";
    public long ExpiresAt { get; set; }
    public long DueAt { get; set; }
    public string State { get; set; } = "Pending";
    public string Reason { get; set; } = "Prepared";
    public int Attempts { get; set; }
    public string? Claim { get; set; }
    public long? LeaseUntil { get; set; }
    public long? ProviderAcceptedAt { get; set; }
}
