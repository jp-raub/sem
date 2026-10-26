// 保全済み旧mainからモデル宣言のみを抽出。サービス・画面・起動処理は含めない。
using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
namespace Sem.Web.Core;
// A personal opt-in, not a device permission, recipient snapshot or delivery queue.
public sealed class PartStartPreference {
 public string EventId {get;set;}=""; public string UserId {get;set;}=""; public string PartId {get;set;}="";
 public bool Enabled {get;set;} public long Version {get;set;} public long ChangedAt {get;set;} public long?ArmedAt{get;set;} public long?ArmedProgressVersion{get;set;}
}
