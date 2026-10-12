namespace Sem.Web.Core;
public sealed class EventPhase
{
 public string Id { get; set; } = Guid.NewGuid().ToString();
 public string EventId { get; set; } = "";
 public string Kind { get; set; } = "Custom";
 public string Name { get; set; } = "";
 public int MaxPicks { get; set; }
 public string? ActivateAt { get; set; }
 public string? DeactivateAt { get; set; }
 public bool IsActive { get; set; }
 public int SortOrder { get; set; }
 public string? DeletedAt { get; set; }
}
