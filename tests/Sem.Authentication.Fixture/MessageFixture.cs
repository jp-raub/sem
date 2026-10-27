using Sem.Web.Data;
using Sem.Web.Runtime;
public static class MessageFixture {
 public static async Task Seed(ApplicationDbContext db){var start=DateTimeOffset.Parse("2030-11-14T03:00:00Z").ToUnixTimeSeconds();for(var n=1;n<=102;n++)db.Messages.Add(new Message{Id=$"m{n:000}",UserId="synthetic-user",Title=n==102?"<img src=x> 合成メッセージ":$"合成メッセージ {n}",Body=n==102?"<script>合成本文</script>\n二行目":"合成本文 "+n,Created=start+n,ReadAt=n%2==0?start+200:null});db.Messages.Add(new Message{Id="m000",UserId="synthetic-user",Title="過去の日時不明",Body="日時の原記録なし"});db.Messages.Add(new Message{Id="other-message",UserId="other",Title="OTHER_TITLE_SENTINEL",Body="OTHER_BODY_SENTINEL",Created=start+999});await db.SaveChangesAsync();}
}
