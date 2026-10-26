// 保全済み旧mainからモデル宣言のみを抽出。サービス・画面・起動処理は含めない。
using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
namespace Sem.Web.Core;
// Explicit arming alone does not send or make historical dormant entries sendable.
public sealed class EventPushRule {
 public string EventId {get;set;}=""; public string Kind {get;set;}="";
 public bool Enabled {get;set;} public long Version {get;set;}
 public long? ArmedAt {get;set;} public long? ArmedProgressVersion {get;set;} public long ChangedAt {get;set;}
}
