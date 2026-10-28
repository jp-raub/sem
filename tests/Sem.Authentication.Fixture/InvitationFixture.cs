using Sem.Web.Data;
using Sem.Web.Runtime;
public static class InvitationFixture {
 public static async Task Seed(ApplicationDbContext db){
  var start=DateTimeOffset.Parse("2030-11-14T03:00:00Z").ToUnixTimeSeconds();
  for(var n=1;n<=102;n++)db.InvitationRequests.Add(new InvitationRequest{Id=$"req-{n:000}",UserId="synthetic-user",Reason=n==102?"<img src=x> 合成の理由":$"合成依頼 {n}",Count=n==102?3:1,Days=7,Status=n==101?"Approved":n==100?"Rejected":"Pending",Created=start+n,DecidedBy=n==101?"other":n==100?"missing-old-user":null,DecidedAt=n==101?start+200:null,DecisionComment=n==101?"<script>管理者コメント</script>":null});
  db.InvitationRequests.Add(new InvitationRequest{Id="req-000",UserId="synthetic-user",Reason="過去の日時不明",Count=1,Days=1,Status="LegacyApproved"});
  foreach(var (id,expires,limit,uses,disabled) in new[]{("active",start+86400,3,1,false),("expired",1L,2,0,false),("full",start+86400,2,2,false),("disabled",start+86400,2,0,true),("unknown",0L,0,0,false)})db.Invitations.Add(new Invitation{Id=id,OwnerId="synthetic-user",IssuerId="other",Created=id=="unknown"?0:start,Expires=expires,Limit=limit,Uses=uses,Disabled=disabled,TokenHash="TOKEN_HASH_SENTINEL-"+id,ProtectedToken="PROTECTED_TOKEN_SENTINEL",Purpose="PRIVATE_PURPOSE_SENTINEL"});
  db.InvitationRequests.Add(new InvitationRequest{Id="other-request",UserId="other",Reason="OTHER_REQUEST_SENTINEL",Created=start+999});
  db.Invitations.Add(new Invitation{Id="other-invitation",OwnerId="other",IssuerId="synthetic-user",TokenHash="OTHER_TOKEN_SENTINEL",ProtectedToken="OTHER_PROTECTED_SENTINEL",Purpose="OTHER_PURPOSE_SENTINEL",Created=start+999});
  await db.SaveChangesAsync();
 }
}
