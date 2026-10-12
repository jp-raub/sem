namespace Sem.Web.Core;
public sealed class Song {
 public string Id {get;set;}=Guid.NewGuid().ToString(); public string EventId {get;set;}="";
 public string Title {get;set;}=""; public string Artist {get;set;}="";
 public int PerformanceSeconds {get;set;}=270; public int? PrepSeconds {get;set;}
 public int SortOrder {get;set;} public string? DeletedAt {get;set;}
}
public sealed class SongPart {
 public string Id {get;set;}=Guid.NewGuid().ToString(); public string EventId {get;set;}="";
 public string SongId {get;set;}=""; public string PartDefinitionId {get;set;}="";
 // Storage foundation only: management DTOs cannot set or clear registrations.
 public string? AssignedUserId {get;set;} public string? PhaseId {get;set;}
}
