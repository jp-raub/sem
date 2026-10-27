using Microsoft.EntityFrameworkCore;
using Sem.Web.Core;
using Sem.Web.Data;
public static class PublicAnswerFixture {
 public static async Task Seed(ApplicationDbContext db){
  db.Users.Add(new(){Id="synthetic-invalid",UserName="invalid@example.invalid",Email="invalid@example.invalid",DisplayName="合成未回答者",Enabled=true,EmailConfirmed=true,SecurityStamp="invalid-stamp"});
  db.EventQuestions.AddRange(new(){Id="summary-yn",EventId="public",Text="合成YES／NO",Type="YesNo",Public=true,SortOrder=0},new(){Id="summary-text",EventId="public",Text="公開希望 <img src=x>",Type="Text",Public=true,PublicText=true,SortOrder=1},new(){Id="summary-private",EventId="public",Text="本文非公開の回答",Type="Text",Public=true,SortOrder=2},new(){Id="summary-empty",EventId="public",Text="回答なし",Type="YesNo",Public=true,SortOrder=3},new(){Id="summary-disabled",EventId="public",Text="PRIVATE_DISABLED_QUESTION",Type="Text",Public=true,PublicText=true,Disabled=true},new(){Id="summary-deleted",EventId="public",Text="PRIVATE_DELETED_QUESTION",Type="Text",Public=true,PublicText=true,DeletedAt="2026-10-26T00:00:00Z"},new(){Id="summary-hidden",EventId="hidden",Text="PRIVATE_HIDDEN_QUESTION",Type="Text",Public=true,PublicText=true});
  foreach(var(id,yn,text,secret) in new[]{("synthetic-user","YES","<img src=x> 希望A","PRIVATE_TEXT_SENTINEL_A"),("synthetic-member","NO","<img src=x> 希望A","\t\u3000\u00a0\n"),("synthetic-watcher","YES","希望B","PRIVATE_TEXT_SENTINEL_B"),("synthetic-invalid","MAYBE","\u2009\u205f", "\u202f\u1680")}){
   db.EventQuestionAnswers.AddRange(new(){QuestionId="summary-yn",UserId=id,Value=yn},new(){QuestionId="summary-text",UserId=id,Value=text},new(){QuestionId="summary-private",UserId=id,Value=secret});
  }
  foreach(var id in new[]{"summary-disabled","summary-deleted","summary-hidden"})db.EventQuestionAnswers.Add(new(){QuestionId=id,UserId="synthetic-user",Value="PRIVATE_UNUSED_ANSWER"});
  await db.SaveChangesAsync();
 }
}
