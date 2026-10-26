// 保全済み旧mainからモデル宣言のみを抽出。サービス・画面・起動処理は含めない。
using Microsoft.EntityFrameworkCore;
using Sem.Web.Data;
using Sem.Web.Runtime;
using System.Security.Claims;
namespace Sem.Web.Core;
public sealed class NotificationScheduleState {
 public string EventId{get;set;}="";public string Fingerprint{get;set;}="";public long EventVersion{get;set;}public long ProgressVersion{get;set;}public long Version{get;set;}public long ChangedAt{get;set;}
}

public sealed class NotificationReservation {
 public string EventId{get;set;}="";public string Id{get;set;}="";public string Kind{get;set;}="";public string?PartId{get;set;}
 public string Basis{get;set;}="";public string?PreviousPartId{get;set;}public long?ReferenceAt{get;set;}public long?DueAt{get;set;}
 public string State{get;set;}="WaitingActual";public string?CancelReason{get;set;}public long Version{get;set;}public long ChangedAt{get;set;}
}
