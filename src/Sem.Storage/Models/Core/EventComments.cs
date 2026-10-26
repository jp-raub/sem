// 保全済み旧mainからモデル宣言のみを抽出。サービス・画面・起動処理は含めない。

namespace Sem.Web.Core;
public sealed class EventComment {
 public string Id {get;set;}="";public string EventId {get;set;}="";public string UserId {get;set;}="";
 public string Body {get;set;}="";public string CreatedAt {get;set;}="";
}

// Retain only hash/result identity after physical deletion, not a second copy of the comment body.
public sealed class CommentOperation {
 public string Id {get;set;}="";public string EventId {get;set;}="";public string ActorId {get;set;}="";
 public string Kind {get;set;}="";public string CommentId {get;set;}="";public string RequestHash {get;set;}="";public string At {get;set;}="";
}
