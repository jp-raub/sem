// 保全済み旧mainからモデル宣言のみを抽出。サービス・画面・起動処理は含めない。

namespace Sem.Web.Core;
public sealed class EventSpectator {
 public string EventId {get;set;}="";public string UserId {get;set;}="";
 public string RegisteredAt {get;set;}="";public string RegisteredBy {get;set;}="";
}

public sealed class RegistrationOperation {
 public string Id {get;set;}="";public string EventId {get;set;}="";
 public string ActorId {get;set;}="";public string SubjectId {get;set;}="";
 public string PhaseId {get;set;}="";public string Kind {get;set;}="";
 public string Reason {get;set;}="";public string At {get;set;}="";
 public string RequestHash {get;set;}="";public string ResultJson {get;set;}="";
}


public sealed class EventParticipantState {
 public string EventId {get;set;}="";public string UserId {get;set;}="";public long RowVersion {get;set;}
}
