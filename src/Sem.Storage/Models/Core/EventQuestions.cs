// 保全済み旧mainからモデル宣言のみを抽出。サービス・画面・起動処理は含めない。

namespace Sem.Web.Core;
public sealed class EventQuestion {
 public string Id {get;set;}=Guid.NewGuid().ToString();public string EventId {get;set;}="";
 public string Text {get;set;}="";public string Type {get;set;}="YesNo";
 public bool Required {get;set;}public bool Disabled {get;set;}public bool Public {get;set;}public bool PublicText {get;set;}
 public int SortOrder {get;set;}public string? DeletedAt {get;set;}
}

// Answers are written only through validated participation operations; management plans never expose values.
public sealed class EventQuestionAnswer {
 public string QuestionId {get;set;}="";public string UserId {get;set;}="";
 public string? Value {get;set;}public string UpdatedAt {get;set;}="";
}
