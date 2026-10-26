using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Sem.Web.Data;

// 全DBは合成データのメモリ内SQLite。実volume・鍵・メール・ネットワークは使用しない。
int checks=0;
void Check(bool value,string name){if(!value)throw new Exception(name);checks++;}
async Task Execute(ApplicationDbContext db,string sql){using var command=db.Database.GetDbConnection().CreateCommand();command.CommandText=sql;await command.ExecuteNonQueryAsync();}
async Task<string> Fingerprint(ApplicationDbContext db){
 var c=db.Database.GetDbConnection();var rows=new List<string>();
 using(var command=c.CreateCommand()){
  command.CommandText="SELECT type,name,tbl_name,sql FROM sqlite_master WHERE name NOT LIKE 'sqlite_%' AND name <> '__EFMigrationsLock' ORDER BY type,name";
  using var reader=await command.ExecuteReaderAsync();while(await reader.ReadAsync())rows.Add(JsonSerializer.Serialize(Enumerable.Range(0,reader.FieldCount).Select(i=>reader.IsDBNull(i)?null:reader.GetValue(i)).ToArray()));
 }
 var tables=new List<string>();using(var command=c.CreateCommand()){command.CommandText="SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' AND name <> '__EFMigrationsLock' ORDER BY name";using var reader=await command.ExecuteReaderAsync();while(await reader.ReadAsync())tables.Add(reader.GetString(0));}
 foreach(var table in tables){using var command=c.CreateCommand();command.CommandText="SELECT * FROM \""+table.Replace("\"","\"\"")+"\"";using var reader=await command.ExecuteReaderAsync();var data=new List<string>();while(await reader.ReadAsync())data.Add(JsonSerializer.Serialize(Enumerable.Range(0,reader.FieldCount).Select(i=>reader.IsDBNull(i)?null:reader.GetValue(i)).ToArray()));rows.Add(table+":"+string.Join("\n",data.Order(StringComparer.Ordinal)));}
 return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n",rows))));
}
async Task Run(Func<ApplicationDbContext,Task> action,string connectionString="Data Source=:memory:"){
 using var connection=new SqliteConnection(connectionString);await connection.OpenAsync();
 var services=new ServiceCollection();services.AddLogging();
 services.AddDbContext<ApplicationDbContext>(o=>o.UseSqlite(connection));
 services.AddIdentityCore<ApplicationUser>(o=>{o.Stores.SchemaVersion=IdentitySchemaVersions.Version3;o.User.RequireUniqueEmail=true;o.SignIn.RequireConfirmedAccount=true;}).AddEntityFrameworkStores<ApplicationDbContext>();
 await using var provider=services.BuildServiceProvider();await using var scope=provider.CreateAsyncScope();var db=scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();await action(db);
}
async Task SeedIdentity(ApplicationDbContext db){
 await Execute(db,"""
 INSERT INTO AspNetUsers (Id,UserName,NormalizedUserName,Email,NormalizedEmail,EmailConfirmed,SecurityStamp,ConcurrencyStamp,PhoneNumberConfirmed,TwoFactorEnabled,LockoutEnabled,AccessFailedCount,DisplayName,Enabled,IsAdmin,Bootstrap)
 VALUES ('synthetic-user','synthetic@example.invalid','SYNTHETIC@EXAMPLE.INVALID','synthetic@example.invalid','SYNTHETIC@EXAMPLE.INVALID',1,'synthetic-security-stamp','synthetic-concurrency',0,0,0,0,'合成ユーザー',1,1,0);
 """);
 // WebAuthnとして有効な秘密や資格情報を作らず、ストアの保持だけを検査する。
 await Execute(db,"INSERT INTO AspNetUserPasskeys (UserId,CredentialId,Data) VALUES ('synthetic-user',X'010203','{\"synthetic\":true}')");
}
await Run(async db=>{
 Check(!db.Database.HasPendingModelChanges(),"snapshotと現行モデルの一致");
 await SqliteStorageInitializer.InitializeAsync(db);
 var known=db.Database.GetMigrations().ToArray();
 Check(known.First()==SqliteStorageInitializer.BaselineId,"既知baseline先頭");
 Check(known.Length==43,"旧43 migrationを全保持");
 Check((await db.Database.GetAppliedMigrationsAsync()).SequenceEqual(known),"全migration適用");
 await SeedIdentity(db);
 db.RevokedAuthSessions.Add(new Sem.Web.Core.RevokedAuthSession { TokenHash=new string('a',64),UserId="synthetic-user",RevokedAt="2026-01-01T00:00:00Z" });
 await db.SaveChangesAsync();
 var before=await Fingerprint(db);await SqliteStorageInitializer.InitializeAsync(db);
 Check(await Fingerprint(db)==before,"再起動時に全schema・全行・passkey・stampを保持");
 Check((await db.Users.SingleAsync()).Id=="synthetic-user","ユーザーID保持");
 Check((await db.Users.SingleAsync()).IsAdmin,"管理者ロール保持");
 Check((await db.RevokedAuthSessions.SingleAsync()).TokenHash==new string('a',64),"セッション失効情報を保持");
});
await Run(async db=>{
 var script=db.GetService<IMigrator>().GenerateScript(Migration.InitialDatabase,SqliteStorageInitializer.BaselineId,MigrationsSqlGenerationOptions.NoTransactions);
 await Execute(db,script);await SeedIdentity(db);
 await Execute(db,"DROP TABLE __EFMigrationsHistory");
 await SqliteStorageInitializer.InitializeAsync(db);
 Check((await db.Users.SingleAsync()).SecurityStamp=="synthetic-security-stamp","履歴なし既知baselineからstamp保持");
 Check((await db.Database.GetAppliedMigrationsAsync()).Count()==43,"既知baseline採用後の全migration");
 using var cmd=db.Database.GetDbConnection().CreateCommand();cmd.CommandText="SELECT COUNT(*) FROM AspNetUserPasskeys WHERE UserId='synthetic-user' AND CredentialId=X'010203' AND Data='{\"synthetic\":true}'";
 Check(Convert.ToInt64(await cmd.ExecuteScalarAsync())==1,"baseline更新でpasskeyストア保持");
});
async Task RejectedUnchanged(ApplicationDbContext db,string name){
 var before=await Fingerprint(db);bool rejected=false;
 try{await SqliteStorageInitializer.InitializeAsync(db);}catch(InvalidOperationException){rejected=true;}
 Check(rejected,name+"の拒否");Check(await Fingerprint(db)==before,name+"でschema・行を変更しない");
}
await Run(async db=>{await Execute(db,"CREATE TABLE PrivateData (Id TEXT PRIMARY KEY, Value TEXT); INSERT INTO PrivateData VALUES ('synthetic','keep')");await RejectedUnchanged(db,"未知のDB");});
await Run(async db=>{await SqliteStorageInitializer.InitializeAsync(db);await Execute(db,"INSERT INTO __EFMigrationsHistory VALUES ('20990101000000_Unknown','10.0.12')");await RejectedUnchanged(db,"未知migration");});
await Run(async db=>{await SqliteStorageInitializer.InitializeAsync(db);await Execute(db,"ALTER TABLE AspNetUsers ADD COLUMN Unsupported TEXT");await RejectedUnchanged(db,"既知履歴・不一致schema");});
await Run(async db=>{await SqliteStorageInitializer.InitializeAsync(db);await Execute(db,"DELETE FROM __EFMigrationsHistory WHERE MigrationId=(SELECT min(MigrationId) FROM __EFMigrationsHistory)");await RejectedUnchanged(db,"途中欠落の履歴");});
var folder=Path.Combine(Path.GetTempPath(),"sem-storage-synthetic-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
try{
 var original=Path.Combine(folder,"original.db");var copy=Path.Combine(folder,"isolated-copy.db");
 await Run(async db=>{
  var script=db.GetService<IMigrator>().GenerateScript(Migration.InitialDatabase,SqliteStorageInitializer.BaselineId,MigrationsSqlGenerationOptions.NoTransactions);
  await Execute(db,script);await SeedIdentity(db);
 },"Data Source="+original+";Pooling=False");
 var before=SHA256.HashData(await File.ReadAllBytesAsync(original));File.Copy(original,copy);
 await Run(async db=>{
  await SqliteStorageInitializer.InitializeAsync(db);
  Check((await db.Users.SingleAsync()).Email=="synthetic@example.invalid","隔離コピー更新でメール保持");
  Check((await db.Database.GetAppliedMigrationsAsync()).Count()==43,"隔離コピーを最新へ更新");
 },"Data Source="+copy+";Pooling=False");
 var after=SHA256.HashData(await File.ReadAllBytesAsync(original));
 Check(before.SequenceEqual(after),"コピー更新が元DBを変更しない");
}finally{Directory.Delete(folder,true);}
Console.WriteLine(JsonSerializer.Serialize(new{checks,scope="合成メモリDBの互換・保護検証。実DB・実鍵・実認証・PostgreSQLの検証ではない。"},new JsonSerializerOptions{Encoder=System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping}));
