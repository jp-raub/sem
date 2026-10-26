// 保全済み旧mainからモデル宣言のみを抽出。サービス・画面・起動処理は含めない。
using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Sem.Web.Runtime;
namespace Sem.Web.Core;

// Immutable accepted request and recipient snapshot. No worker consumes this outbox yet.
public sealed class ManualNotificationOperation
{
    public string Id { get; set; } = "";
    public string EventId { get; set; } = "";
    public string ActorId { get; set; } = "";
    public string RequestHash { get; set; } = "";
    public string PreviewFingerprint { get; set; } = "";
    public string Subject { get; set; } = "";
    public string Body { get; set; } = "";
    public int RecipientCount { get; set; }
    public long AcceptedAt { get; set; }
}

public sealed class ManualNotificationRecipient
{
    // Same deterministic ID as its inbox message. Snapshot plus dormant recipient-level Push outbox.
    public string Id { get; set; } = "";
    public string OperationId { get; set; } = "";
    public string EventId { get; set; } = "";
    public string UserId { get; set; } = "";
    public string PushState { get; set; } = "Pending";
}
