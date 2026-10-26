// 保全済み旧mainからモデル宣言のみを抽出。サービス・画面・起動処理は含めない。

namespace Sem.Web.Core;
public sealed class PartPresetState { public int Id {get;set;}=1; public long RowVersion {get;set;} }

public sealed class PartPreset {
 public string Id {get;set;}=Guid.NewGuid().ToString(); public string Name {get;set;}="";
 public string NormalizedName {get;set;}=""; public string Category {get;set;}="Play";
 public int SortOrder {get;set;} public string? DeletedAt {get;set;}
}

public sealed class EventPartDefinition {
 public string Id {get;set;}=Guid.NewGuid().ToString(); public string EventId {get;set;}="";
 public string? SourcePresetId {get;set;} public string Name {get;set;}="";
 public string NormalizedName {get;set;}=""; public string Category {get;set;}="Play";
 public int SortOrder {get;set;} public string? DeletedAt {get;set;}
}
