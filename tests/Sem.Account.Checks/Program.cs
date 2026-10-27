using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sem.Authentication;
using Sem.Web.Core;
using Sem.Web.Data;
using Sem.Web.Runtime;
int checks=0;void Check(bool ok,string label){if(!ok)throw new Exception(label);checks++;}
var file=Path.Combine(Path.GetTempPath(),"sem-account-"+Guid.NewGuid().ToString("N")+".db");
try{
 var services=new ServiceCollection();services.AddLogging();services.AddDbContext<ApplicationDbContext>(o=>o.UseSqlite("Data Source="+file));services.AddIdentityCore<ApplicationUser>(o=>o.Stores.SchemaVersion=IdentitySchemaVersions.Version3).AddEntityFrameworkStores<ApplicationDbContext>();await using var provider=services.BuildServiceProvider();await using var scope=provider.CreateAsyncScope();var db=scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();var users=scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();await SqliteStorageInitializer.InitializeAsync(db);
 var actor=new ApplicationUser{Id="actor",UserName="actor@example.invalid",Email="actor@example.invalid",EmailConfirmed=true,Enabled=true,DisplayName="合成本人",SecurityStamp="stamp"};var other=new ApplicationUser{Id="other",UserName="other@example.invalid",Email="other@example.invalid",EmailConfirmed=true,Enabled=true,DisplayName="PRIVATE_OTHER",SecurityStamp="other-stamp"};db.Users.AddRange(actor,other);await db.SaveChangesAsync();
 UserPasskeyInfo Key(byte id,string? name,DateTimeOffset at)=>new([id],[11,22,33],at,777,["internal"],true,true,true,[44,55],[66,77]){Name=name};
 var at=DateTimeOffset.Parse("2026-10-27T12:00:00+09:00");Check((await users.AddOrUpdatePasskeyAsync(actor,Key(1,"<img src=x> 本人キー",at))).Succeeded,"標準ストアに合成本人キー");Check((await users.AddOrUpdatePasskeyAsync(actor,Key(2,null,default))).Succeeded,"名称・日時欠損の合成キー");Check((await users.AddOrUpdatePasskeyAsync(other,Key(3,"PRIVATE_OTHER_KEY",at))).Succeeded,"他人のキーを分離");
 var identity=new IdentityOptions();ClaimsPrincipal Who(string id="actor",string stamp="stamp",string? session="session",bool duplicate=false)=>new(new ClaimsIdentity(new[]{new Claim(identity.ClaimsIdentity.UserIdClaimType,id),new Claim(identity.ClaimsIdentity.SecurityStampClaimType,stamp),new Claim(ClaimTypes.Role,"ServiceAdmin")}.Concat(session is null?[]:[new Claim(AuthenticationSession.SessionClaim,session)]).Concat(duplicate?[new Claim(identity.ClaimsIdentity.UserIdClaimType,id)]:[]),"synthetic"));
 Task<AccountReadResult> Read(ClaimsPrincipal? who=null)=>AccountReader.ReadAsync(db,users,who??Who(),identity);
 var before=File.ReadAllBytes(file);var wal=File.Exists(file+"-wal")?File.ReadAllBytes(file+"-wal"):[];var result=await Read();var v=result.View!;
 Check(result.State=="ok"&&v.ActorId==actor.Id&&v.Email==actor.Email,"本人の保存済みメールだけ参照");Check(v.SiteName=="SESSION ROOM","サイト未設定の固定名");Check(v.Passkeys.Length==2&&v.Passkeys.Any(k=>k.Name=="<img src=x> 本人キー"),"キー名は文字列のまま投影");Check(v.Passkeys.Single(k=>k.Name!="").CreatedAt=="2026-10-27T03:00:00.0000000+00:00","保存済み登録時刻をUTCへ正規化");Check(v.Passkeys.Single(k=>k.Name=="").CreatedAt is null,"欠損日時を現在時刻で補わない");
 var json=JsonSerializer.Serialize(v);foreach(var privateValue in new[]{"PRIVATE_OTHER","other@example.invalid","PRIVATE_OTHER_KEY","CredentialId","PublicKey","SignCount","Transports","SecurityStamp","ConcurrencyStamp","AttestationObject","ClientDataJson","TokenHash"})Check(!json.Contains(privateValue),"用途外のデータなし "+privateValue);
 Check(before.SequenceEqual(File.ReadAllBytes(file))&&wal.SequenceEqual(File.Exists(file+"-wal")?File.ReadAllBytes(file+"-wal"):[]),"DB／WALを参照で変更しない");Check(!db.ChangeTracker.HasChanges(),"キー最終利用や監査等を参照で更新しない");
 Check((await Read(new(new ClaimsIdentity()))).State=="unauthorized","匿名拒否");Check((await Read(Who(stamp:"old"))).State=="unauthorized","古いstamp拒否");Check((await Read(Who(duplicate:true))).State=="unauthorized","多重本人claim拒否");Check((await Read(Who(session:null))).State=="unauthorized","sessionなし拒否");Check((await Read(Who(id:"missing"))).State=="unauthorized","存在しない本人拒否");
 var otherView=(await Read(Who("other","other-stamp"))).View!;Check(otherView.Email==other.Email&&otherView.Passkeys.Single().Name=="PRIVATE_OTHER_KEY","別本人は別のメール・キーだけ");
 actor.Enabled=false;await db.SaveChangesAsync();Check((await Read()).State=="unauthorized","無効化を即反映");actor.Enabled=true;actor.EmailConfirmed=false;await db.SaveChangesAsync();Check((await Read()).State=="unauthorized","メール未確認を拒否");actor.EmailConfirmed=true;await db.SaveChangesAsync();
 await users.RemovePasskeyAsync(actor,[1]);Check((await Read()).View!.Passkeys.Length==1,"別操作で消えたキーを参照へ残さない");await users.RemovePasskeyAsync(actor,[2]);Check((await Read()).View!.Passkeys.Length==0,"0件を架空サンプルで補充しない");
 db.RevokedAuthSessions.Add(new(){UserId=actor.Id,TokenHash=AuthenticationSession.Hash("session"),RevokedAt=DateTimeOffset.UtcNow.ToString("O")});await db.SaveChangesAsync();Check((await Read()).State=="unauthorized","失効sessionで私有メールとキーを返さない");
 Console.WriteLine($"アカウント本人参照・最小投影・失効 {checks}検査: 成功（合成DBのみ）");
}finally{Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();foreach(var p in new[]{file,file+"-wal",file+"-shm"})File.Delete(p);}
