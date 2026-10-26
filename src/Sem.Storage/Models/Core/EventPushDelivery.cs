// 保全済み旧mainからモデル宣言のみを抽出。サービス・画面・起動処理は含めない。
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Sem.Web.Data;
using Sem.Web.Runtime;
namespace Sem.Web.Core;
public sealed class EventPushDelivery {
 public string Id{get;set;}="";public string EventId{get;set;}="";public string EntryId{get;set;}="";public string Kind{get;set;}="";
 public string UserId{get;set;}="";public string DeviceId{get;set;}="";public string DeviceFingerprint{get;set;}="";public string SourceFingerprint{get;set;}="";
 public long RuleVersion{get;set;}public long Due{get;set;}public long Expires{get;set;}public string Status{get;set;}="Pending";
 public int Attempts{get;set;}public string?Claim{get;set;}public long?LeaseUntil{get;set;}public long?AcceptedAt{get;set;}public string?Error{get;set;}
}
