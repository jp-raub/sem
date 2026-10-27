using Sem.Web.Core;
using Sem.Web.Data;
// 隔離DB以外へ登録しない合成プロフィール。
public static class ProfileFixture {
 public static async Task Seed(ApplicationDbContext db){
  foreach(var id in new[]{"synthetic-user","synthetic-member"}){
   db.UserProfiles.Add(new(){UserId=id,Reading="ごうせい",CallName="サンプル",Icon="🎸",SocialJson="[{\"Service\":\"合成SNS\",\"Url\":\"https://example.invalid/member?x=1&y=2\"},{\"Service\":\"危険\",\"Url\":\"javascript:alert(1)\"},{\"Service\":\"資格情報\",\"Url\":\"https://private:secret@example.invalid\"}]"});
   db.ProfileAnswers.AddRange(new ProfileAnswer{UserId=id,QuestionId="profile-text",Value="<img src=x> 音楽\n一緒に演奏",UpdatedAt="2026-10-26T00:00:00Z"},new ProfileAnswer{UserId=id,QuestionId="profile-no",Value="NO",UpdatedAt="2026-10-26T00:00:00Z"},new ProfileAnswer{UserId=id,QuestionId="profile-disabled",Value="PRIVATE_DISABLED_ANSWER",UpdatedAt="2026-10-26T00:00:00Z"});
  }
  db.ProfileQuestions.AddRange(new ProfileQuestion{Id="profile-text",Text="好きな曲 <script>",Type="Text",SortOrder=0},new ProfileQuestion{Id="profile-no",Text="経験あり",Type="YesNo",SortOrder=1},new ProfileQuestion{Id="profile-empty",Text="任意の自己紹介",Type="Text",SortOrder=2},new ProfileQuestion{Id="profile-disabled",Text="PRIVATE_DISABLED_QUESTION",Enabled=false});
  db.ProfileImages.Add(new(){UserId="synthetic-member",RowVersion=1,Width=1,Height=1,Data=Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVQIHWP4z8DwHwAFgAI/ScLttAAAAABJRU5ErkJggg==")});
  db.EventSpectators.Add(new(){EventId="hidden",UserId="synthetic-member",RegisteredBy="synthetic-user"});
  await db.SaveChangesAsync();
 }
}
