using Sem.Web.Core;
using Sem.Web.Data;
// 合成データのみ。製品アセンブリから参照しない。
public static class EventListFixture
{
 public static SessionEvent Event(string id,string date,string kind="View",bool manual=true) {
  var e=new SessionEvent{Id=id,Title=id,Location="合成スタジオ",ScheduledDate=date,Description="PRIVATE_DESCRIPTION_SENTINEL",AnnouncementMessage="一覧のお知らせ",ManualPhaseOverride=manual,PhaseKind="Hidden"};
  e.Phases=[new(){Id=id+"-hidden",EventId=id,Kind="Hidden",Name="非公開",IsActive=kind=="Hidden"},new(){Id=id+"-view",EventId=id,Kind="View",Name="閲覧のみ",IsActive=kind=="View"},new(){Id=id+"-custom",EventId=id,Kind="Custom",Name="参加登録中",MaxPicks=-1,IsActive=kind=="Custom",ActivateAt="2020-01-01T00:00:00Z"}];return e;
 }
 public static async Task Seed(ApplicationDbContext db) {
  var publicEvent=Event("public","2030-11-14T13:00:00+09:00","Custom");publicEvent.Title="秋のセッション <img src=x>";publicEvent.AnnouncementMessage="<script>合成のお知らせ</script>";
  var view=Event("view","2030-10-01T12:00:00+09:00");view.Title="音楽セッション";
  var managed=Event("managed","2031-01-02T13:00:00+09:00","Hidden");managed.Title="管理中の非公開イベント";managed.Admins=[new(){EventId="managed",UserId="synthetic-user"}];
  var hidden=Event("hidden","2032-01-02T13:00:00+09:00","Hidden");hidden.Title="HIDDEN_SENTINEL";hidden.PhaseKind="View";
  var automatic=Event("automatic","2001-01-01T00:00:00Z","Hidden",false);automatic.Title="過去のイベント";automatic.Phases.Single(p=>p.Kind=="Custom").ActivateAt=null;
  var invalid=Event("invalid","timezoneなし");
  db.SessionEvents.AddRange(publicEvent,view,managed,hidden,automatic,invalid);
  db.ServiceSettings.Add(new(){Name="合成セッション"});
  db.Songs.Add(new(){Id="song",EventId="public",Title="合成曲"});
  db.EventPartDefinitions.AddRange(new EventPartDefinition{Id="vo",EventId="public",Name="Vo",NormalizedName="VO"},new EventPartDefinition{Id="deleted",EventId="public",Name="削除済",NormalizedName="DELETED",DeletedAt="2020-01-01T00:00:00Z"});
  db.TimeParts.Add(new(){Id="time",EventId="public",SongId="song",Kind="Song"});
  db.SongParts.AddRange(new SongPart{Id="free",EventId="public",SongId="song",PartDefinitionId="vo"},new SongPart{Id="own",EventId="public",SongId="song",PartDefinitionId="vo",AssignedUserId="synthetic-user",PhaseId="public-custom"},new SongPart{Id="ignored",EventId="public",SongId="song",PartDefinitionId="deleted"});
  await db.SaveChangesAsync();
 }
}
