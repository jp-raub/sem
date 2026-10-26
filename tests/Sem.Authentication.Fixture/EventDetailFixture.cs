using Microsoft.EntityFrameworkCore;
using Sem.Web.Core;
using Sem.Web.Data;
// 名前・メール・画像・回答はすべて隔離検証だけの合成値。
public static class EventDetailFixture {
 public static async Task Seed(ApplicationDbContext db){
  db.Users.AddRange(new(){Id="synthetic-member",UserName="member@example.invalid",Email="member@example.invalid",DisplayName="合成参加者",Enabled=true,EmailConfirmed=true,SecurityStamp="member-stamp"},new(){Id="synthetic-watcher",UserName="watcher@example.invalid",Email="watcher@example.invalid",DisplayName="合成観覧者",Enabled=true,EmailConfirmed=true,SecurityStamp="watcher-stamp"});
  var e=await db.SessionEvents.SingleAsync(x=>x.Id=="public");e.OpensAt="2030-11-14T03:30:00Z";e.LocationUrl="https://example.invalid/venue?x=1&y=2";e.DescriptionFormat="html";e.Description="<h1>合成イベント概要</h1><p><strong>音楽</strong>でつながる。</p><script>PRIVATE_ACTIVE_SCRIPT</script><img src='https://example.invalid/x' onerror='alert(1)'><a href='javascript:alert(1)'>危険リンク</a><a href='https://example.invalid/info'>案内</a>";e.DefaultPrepSeconds=61;e.PlannedEndAt="2030-11-14T04:01:00Z";
  var song=await db.Songs.SingleAsync();song.PerformanceSeconds=61;song.PrepSeconds=null;song.Artist="合成アーティスト";
  db.Songs.Add(new(){Id="second-song",EventId="public",Title="次の合成曲",Artist="別のアーティスト",PerformanceSeconds=1,PrepSeconds=0,SortOrder=1});
  db.EventPartDefinitions.AddRange(new EventPartDefinition{Id="gt",EventId="public",Name="Gt",NormalizedName="GT",SortOrder=1},new EventPartDefinition{Id="key",EventId="public",Name="Key",NormalizedName="KEY",SortOrder=2});
  db.TimeParts.AddRange(new TimePart{Id="transition",EventId="public",Kind="Transition",Name="合成の進行",DurationSeconds=31,SortOrder=1},new TimePart{Id="second-time",EventId="public",Kind="Song",SongId="second-song",SortOrder=2});
  db.SongParts.Add(new(){Id="second-slot",EventId="public",SongId="second-song",PartDefinitionId="gt",AssignedUserId="synthetic-member",PhaseId="public-custom"});
  db.EventSpectators.AddRange(new EventSpectator{EventId="public",UserId="synthetic-watcher",RegisteredBy="synthetic-user"},new EventSpectator{EventId="public",UserId="synthetic-member",RegisteredBy="synthetic-user"});
  db.EventComments.Add(new(){Id="public-comment",EventId="public",UserId="synthetic-member",Body="<img src=x>よろしく\nお願いします",CreatedAt="2026-10-26T03:00:00Z"});
  db.EventQuestions.Add(new(){Id="private-question",EventId="public",Text="PRIVATE_QUESTION_SENTINEL",Type="Text",Public=false,PublicText=false});
  db.EventQuestionAnswers.Add(new(){QuestionId="private-question",UserId="synthetic-member",Value="PRIVATE_ANSWER_SENTINEL"});
  db.EventHeaderImages.Add(new(){EventId="public",Width=1,Height=1,Data=Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVQIHWP4z8DwHwAFgAI/ScLttAAAAABJRU5ErkJggg==")});
  await db.SaveChangesAsync();
 }
}
