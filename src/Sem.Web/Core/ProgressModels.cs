namespace Sem.Web.Core;
public sealed class EventProgressState {
 public string EventId {get;set;}="";public long RowVersion {get;set;}
 public long SourceVersion {get;set;} public string PlanJson {get;set;}="{}";
 public string CreatedAt {get;set;}="";
}
public sealed class ProgressMark {
 public string EventId {get;set;}="";public string PartId {get;set;}="";public string Point {get;set;}="";
 public string At {get;set;}="";public string ActorId {get;set;}="";public string OperationId {get;set;}="";
}
public sealed class ProgressEstimate {
 public string EventId {get;set;}="";public string PartId {get;set;}="";
 public int? PrepSeconds {get;set;} public int? DurationSeconds {get;set;}
}
public sealed class ProgressOperation {
 public string Id {get;set;}=Guid.NewGuid().ToString(); public string EventId {get;set;}="";
 public string ActorId {get;set;}="";public string Kind {get;set;}="";public string At {get;set;}="";
 public string Fingerprint {get;set;}="";public string ResultJson {get;set;}="{}";
}
