// 保全済み旧mainからモデル宣言のみを抽出。サービス・画面・起動処理は含めない。

namespace Sem.Web.Core;
// Opening is derived from event overview; never a mutable or persisted time part.
public sealed class TimePart {
 public string Id {get;set;}=Guid.NewGuid().ToString(); public string EventId {get;set;}="";
 public string Kind {get;set;}="Transition"; public string? SongId {get;set;}
 public string Name {get;set;}=""; public int DurationSeconds {get;set;}
 public string? Icon {get;set;} public int SortOrder {get;set;} public string? DeletedAt {get;set;}
}
