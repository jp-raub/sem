namespace Sem.Web.Core;
// Only manually confirmed receipts are introduced here; QR scans remain a separate later model.
public sealed class EventReceptionState {
 public string EventId{get;set;}="";public string UserId{get;set;}="";public long RowVersion{get;set;}
}
public sealed class EventReception {
 public string Id{get;set;}="";public string EventId{get;set;}="";public string UserId{get;set;}="";
 public string At{get;set;}="";public string ActorId{get;set;}="";public string Method{get;set;}="Manual";public string Note{get;set;}="";
 public string? CancelledAt{get;set;} public string? CancelledBy{get;set;}
}
public sealed class ReceptionOperation {
 public string Id{get;set;}="";public string EventId{get;set;}="";public string ActorId{get;set;}="";
 public string RequestHash{get;set;}="";public string ReceptionId{get;set;}="";public string Kind{get;set;}="";public string At{get;set;}="";
}
