// 保全済み旧mainからモデル宣言のみを抽出。サービス・画面・起動処理は含めない。
using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
namespace Sem.Web.Core;

public sealed class ManualNotificationPushActivationOperation
{
    public string Id { get; set; } = "";
    public string ActorId { get; set; } = "";
    public bool Enabled { get; set; }
    public long ExpectedEpoch { get; set; }
    public long ResultEpoch { get; set; }
    public bool Changed { get; set; }
    public long At { get; set; }
}
