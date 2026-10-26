// 保全済み旧mainからモデル宣言のみを抽出。サービス・画面・起動処理は含めない。

namespace Sem.Web.Core;
public sealed class EventParticipantQr {
 public string EventId{get;set;}="";public string UserId{get;set;}="";public long RowVersion{get;set;}
 public string TokenHash{get;set;}="";public string ProtectedToken{get;set;}="";
}

public sealed class EventQrRead {
 public string Id{get;set;}="";public string EventId{get;set;}="";public string UserId{get;set;}="";public string ActorId{get;set;}="";public long TokenVersion{get;set;} public string At{get;set;}="";
}

public sealed class EventQrOperation {
 public string Id{get;set;}="";public string EventId{get;set;}="";public string ActorId{get;set;}="";public string Kind{get;set;}="";public string RequestHash{get;set;}="";public string ResultJson{get;set;}="";
}
