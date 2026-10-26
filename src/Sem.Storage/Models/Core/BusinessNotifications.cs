// 保全済み旧mainからモデル宣言のみを抽出。サービス・画面・起動処理は含めない。
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Sem.Web.Data;
using Sem.Web.Runtime;
namespace Sem.Web.Core;

// One durable email per committed registration/release operation. No Push rules yet.
public sealed class BusinessNotification {
 public string Id {get;set;}="";
 public string EventId {get;set;}="";
 public string UserId {get;set;}="";
 public string Kind {get;set;}="";
 public string CreatedAt {get;set;}="";
 public string ProtectedPayload {get;set;}="";
 public long Due {get;set;}
 public string Status {get;set;}="Pending";
 public int Attempts {get;set;}
 public string? Claim {get;set;}
 public long? LeaseUntil {get;set;}
 public long? SentAt {get;set;}
 public string? Error {get;set;}
}
