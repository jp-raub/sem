using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sem.Web.Core;
using Sem.Web.Data;
using Sem.Web.Runtime;

// Local synthetic storage only. No worker, SMTP, Push, HTTP or real credential is started.
var root=Path.Combine(Path.GetTempPath(),"sem-acceptance-retention-"+Guid.NewGuid().ToString("N"));
var original=Path.Combine(root,"original");var backup=Path.Combine(root,"backup");var restored=Path.Combine(root,"restored");
Directory.CreateDirectory(original);int checks=0;
void Check(bool value,string label){if(!value)throw new Exception(label);Console.WriteLine("PASS "+label);checks++;}
ServiceProvider Provider(string directory){var s=new ServiceCollection();s.AddLogging();s.AddDbContext<ApplicationDbContext>(o=>o.UseSqlite("Data Source="+Path.Combine(directory,"sem.db")));s.AddIdentityCore<ApplicationUser>(o=>o.Stores.SchemaVersion=IdentitySchemaVersions.Version3).AddEntityFrameworkStores<ApplicationDbContext>();s.AddDataProtection().SetApplicationName("SEM.ExternalTest").PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(directory,"keys")));return s.BuildServiceProvider();}
void CopyDirectory(string source,string target){Directory.CreateDirectory(target);foreach(var d in Directory.GetDirectories(source,"*",SearchOption.AllDirectories))Directory.CreateDirectory(Path.Combine(target,Path.GetRelativePath(source,d)));foreach(var f in Directory.GetFiles(source,"*",SearchOption.AllDirectories))File.Copy(f,Path.Combine(target,Path.GetRelativePath(source,f)));}
Dictionary<string,string> FileHashes(string dir)=>Directory.GetFiles(dir,"*",SearchOption.AllDirectories).Order().ToDictionary(f=>Path.GetRelativePath(dir,f),f=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f))));
async Task<string> Dump(string directory){await using var c=new SqliteConnection("Data Source="+Path.Combine(directory,"sem.db"));await c.OpenAsync();var names=new List<string>();using(var cmd=c.CreateCommand()){cmd.CommandText="SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name";using var reader=await cmd.ExecuteReaderAsync();while(await reader.ReadAsync())names.Add(reader.GetString(0));}var result=new List<object>();foreach(var name in names){using var cmd=c.CreateCommand();cmd.CommandText="SELECT * FROM \""+name.Replace("\"","\"\"")+"\"";using var reader=await cmd.ExecuteReaderAsync();var rows=new List<string>();while(await reader.ReadAsync()){var row=new object[reader.FieldCount];reader.GetValues(row);rows.Add(JsonSerializer.Serialize(row.Select(x=>x is DBNull?null:x)));}result.Add(new{table=name,rows=rows.Order(StringComparer.Ordinal).ToArray()});}return JsonSerializer.Serialize(result);}
try{
 string protectedText;
 using(var provider=Provider(original)){
  var db=provider.GetRequiredService<ApplicationDbContext>();await SqliteStorageInitializer.InitializeAsync(db);
  db.Users.Add(new ApplicationUser{Id="member",UserName="fixture@example.test",Email="fixture@example.test",EmailConfirmed=true,Enabled=true,DisplayName="Synthetic member",SecurityStamp="synthetic-stamp"});
  db.SiteStates.Add(new SiteState{Initialized=true,Name="Acceptance synthetic"});
  var e=new SessionEvent{Id="event",Title="Persistent event",ScheduledDate="2099-01-02T03:00:00Z",CreatedAt="2026-10-17T00:00:00Z",RowVersion=3,ManualPhaseOverride=true};
  e.Phases.Add(new(){Id="phase",EventId="event",Kind="Custom",Name="Open",MaxPicks=-1,IsActive=true});db.SessionEvents.Add(e);
  db.EventPartDefinitions.Add(new(){Id="part",EventId="event",Name="Voice",NormalizedName="VOICE",Category="Sing"});
  db.Songs.Add(new Song{Id="song",EventId="event",Title="Synthetic song",PerformanceSeconds=91});
  db.SongParts.Add(new SongPart{Id="slot",EventId="event",SongId="song",PartDefinitionId="part",AssignedUserId="member",PhaseId="phase"});
  db.TimeParts.Add(new TimePart{Id="time",EventId="event",Kind="Song",SongId="song"});
  db.EventQuestions.Add(new(){Id="question",EventId="event",Text="Required?",Type="YesNo",Required=true});
  db.EventQuestionAnswers.Add(new(){QuestionId="question",UserId="member",Value="YES",UpdatedAt=e.CreatedAt});
  db.EventParticipantStates.Add(new(){EventId="event",UserId="member",RowVersion=5});
  db.EventComments.Add(new(){Id="comment",EventId="event",UserId="member",Body="Retained synthetic comment",CreatedAt=e.CreatedAt});
  protectedText=provider.GetRequiredService<IDataProtectionProvider>().CreateProtector("synthetic-retention").Protect("synthetic proof, not a real credential");
  db.Invitations.Add(new(){Id="invite",OwnerId="member",IssuerId="member",TokenHash="synthetic-hash",ProtectedToken=protectedText,Limit=3,Uses=1,Expires=9999999999});
  db.Messages.Add(new(){UserId="member",Title="Synthetic message",Body="retained"});
  await db.SaveChangesAsync();
  await db.Database.ExecuteSqlRawAsync("INSERT INTO AspNetUserPasskeys(CredentialId,UserId,Data) VALUES (X'01020304','member',{0})", "{\"synthetic\":true}");
  Directory.CreateDirectory(Path.Combine(original,"profile-images"));await File.WriteAllBytesAsync(Path.Combine(original,"profile-images","synthetic.bin"),new byte[]{1,2,3,4});
  Check(await db.SongParts.AnyAsync(x=>x.AssignedUserId=="member")&&await db.EventQuestionAnswers.AnyAsync(x=>x.Value=="YES"),"Populated fixture contains a real assignment and answer row");
 }
 SqliteConnection.ClearAllPools();var before=await Dump(original);SqliteConnection.ClearAllPools();
 using(var provider=Provider(original)){var db=provider.GetRequiredService<ApplicationDbContext>();await SqliteStorageInitializer.InitializeAsync(db);Check(before==await Dump(original),"Fresh service provider / repeated startup preserves every table and migration row");}
 SqliteConnection.ClearAllPools();CopyDirectory(original,backup);CopyDirectory(backup,restored);
 Check(JsonSerializer.Serialize(FileHashes(original))==JsonSerializer.Serialize(FileHashes(restored)),"Stopped full-directory backup/restore preserves every byte including keys and private files");
 using(var provider=Provider(restored)){
  var db=provider.GetRequiredService<ApplicationDbContext>();await SqliteStorageInitializer.InitializeAsync(db);
  Check(before==await Dump(restored),"Restored application storage preserves all table values and passkey bytes");
  var protector=provider.GetRequiredService<IDataProtectionProvider>().CreateProtector("synthetic-retention");
  Check(protector.Unprotect(protectedText)=="synthetic proof, not a real credential","Persisted Data Protection keys decrypt pre-restart material after restore");
  Check(await db.Database.SqlQueryRaw<string>("SELECT integrity_check AS Value FROM pragma_integrity_check").SingleAsync()=="ok","Restored SQLite integrity check succeeds");
  Check(await db.Database.SqlQueryRaw<int>("SELECT count(*) AS Value FROM pragma_foreign_key_check").SingleAsync()==0,"Restored SQLite foreign keys remain valid");
  Check((await db.Database.GetAppliedMigrationsAsync()).SequenceEqual(db.Database.GetMigrations()),"Restored startup has complete current migration history");
  Check(!db.Database.HasPendingModelChanges(),"Restored Identity V3 model matches migration snapshot");
 }
 Check(File.ReadAllBytes(Path.Combine(restored,"profile-images","synthetic.bin")).SequenceEqual(new byte[]{1,2,3,4}),"Private profile file survives complete storage restore");
 Console.WriteLine($"Acceptance retention checks: {checks} passed; synthetic storage only");
}finally{SqliteConnection.ClearAllPools();Directory.Delete(root,true);}
