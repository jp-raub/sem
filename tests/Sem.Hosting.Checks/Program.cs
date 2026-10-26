using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sem.Authentication;
using Sem.Hosting;
using Sem.Web.Data;
using Microsoft.AspNetCore.Identity;
int checks=0;
void Check(bool v,string label){if(!v)throw new Exception(label);checks++;}
async Task Rejected(Func<Task> run,string label){try{await run();}catch(InvalidOperationException e){Check(e.Message.StartsWith("SEM_PRODUCT_"),label);return;}throw new Exception("拒否されません: "+label);}
string Hash(string file){using var stream=File.OpenRead(file);return Convert.ToHexString(SHA256.HashData(stream));}
if(!OperatingSystem.IsLinux())throw new Exception("Linuxの隔離環境で実行してください");
var root=Path.Combine(Path.GetTempPath(),"sem-hosting-check-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root,UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute);
try {
 var data=Path.Combine(root,"data");var keys=Path.Combine(data,"keys");var backup=Path.Combine(root,"backup");Directory.CreateDirectory(data);Directory.CreateDirectory(keys);Directory.CreateDirectory(backup);
 var dbPath=Path.Combine(data,"synthetic.sqlite");var settings=new ProductSettings(dbPath,backup,new("https://localhost:19876",keys));
 var map=new Dictionary<string,string>{{"SEM_APPLICATION_MODE","authenticated"},{"SEM_AUTH_ORIGIN","https://localhost:19876"},{"SEM_AUTH_DATABASE",dbPath},{"SEM_AUTH_KEYS_DIRECTORY",keys},{"SEM_AUTH_BACKUP_DIRECTORY",backup}};
 string? Env(string name)=>map.GetValueOrDefault(name);
 Check(ProductSettings.Load(_=>null) is null,"既定はDB非接触demo");
 Check(ProductSettings.Load(Env)!.Database==dbPath,"明示設定");
 foreach(var name in map.Keys.ToArray()) {var saved=map[name];map.Remove(name);await Rejected(()=>{ProductSettings.Load(Env);return Task.CompletedTask;},"必須設定の欠落 "+name);map[name]=saved;}
 map["SEM_APPLICATION_MODE"]="demo";await Rejected(()=>{ProductSettings.Load(Env);return Task.CompletedTask;},"demoに実配置値を混在しない");map["SEM_APPLICATION_MODE"]="authenticated";
 foreach(var invalid in new[]{data,keys,Path.Combine(data,"backups"),root}) {map["SEM_AUTH_BACKUP_DIRECTORY"]=invalid;await Rejected(()=>{ProductSettings.Load(Env);return Task.CompletedTask;},"保全先の重複拒否");}map["SEM_AUTH_BACKUP_DIRECTORY"]=backup;
 map["SEM_AUTH_TRUSTED_PROXIES"]="not-ip";await Rejected(()=>{ProductSettings.Load(Env);return Task.CompletedTask;},"不正プロキシ拒否");map.Remove("SEM_AUTH_TRUSTED_PROXIES");
 await Rejected(async()=>{using var runtime=await ProductStorage.OpenAsync(settings);},"DBなしで新規作成しない");Check(!File.Exists(dbPath),"拒否でDBを作らない");
 var seedServices=new ServiceCollection();seedServices.AddLogging();seedServices.AddDbContext<ApplicationDbContext>(o=>o.UseSqlite(ProductStorage.ConnectionString(dbPath,SqliteOpenMode.ReadWriteCreate)));seedServices.AddIdentityCore<ApplicationUser>(o=>o.Stores.SchemaVersion=IdentitySchemaVersions.Version3).AddEntityFrameworkStores<ApplicationDbContext>();
 await using(var seedProvider=seedServices.BuildServiceProvider()) {
  await using var seedScope=seedProvider.CreateAsyncScope();var db=seedScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
  await SqliteStorageInitializer.InitializeAsync(db);
  db.Users.Add(new(){Id="synthetic",UserName="synthetic@example.invalid",Email="synthetic@example.invalid",EmailConfirmed=true,SecurityStamp="synthetic-stamp",DisplayName="合成人物",Enabled=true});await db.SaveChangesAsync();
 }
 var services=new ServiceCollection();services.AddDataProtection().SetApplicationName(AuthenticationPipeline.ProtectionApplicationName).PersistKeysToFileSystem(new DirectoryInfo(keys));using(var sp=services.BuildServiceProvider())sp.GetRequiredService<IDataProtectionProvider>().CreateProtector("fixture").Protect("synthetic-only");
 var before=Hash(dbPath);var beforeKeys=Directory.GetFiles(keys).ToDictionary(p=>Path.GetFileName(p)!,p=>Hash(p));
 using(var runtime=await ProductStorage.OpenAsync(settings)) {
  Check(Hash(dbPath)==before,"原DBバイト不変");
  Check(Directory.GetFiles(keys).All(p=>beforeKeys[Path.GetFileName(p)]==Hash(p)),"原鍵バイト不変");
  Check(Directory.GetFiles(backup,"verified.json",SearchOption.AllDirectories).Length==1,"検証済み保全1式");
  var marker=Directory.GetFiles(backup,"verified.json",SearchOption.AllDirectories).Single();var snapshot=Path.GetDirectoryName(marker)!;
  Check((File.GetUnixFileMode(snapshot)&(UnixFileMode.GroupRead|UnixFileMode.GroupWrite|UnixFileMode.OtherRead|UnixFileMode.OtherWrite|UnixFileMode.OtherExecute|UnixFileMode.GroupExecute))==0,"保全directoryは所有者のみ");
  foreach(var f in Directory.GetFiles(snapshot,"*",SearchOption.AllDirectories))Check((File.GetUnixFileMode(f)&(UnixFileMode.GroupRead|UnixFileMode.GroupWrite|UnixFileMode.OtherRead|UnixFileMode.OtherWrite))==0,"保全fileは所有者のみ");
  await using var restored=new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(ProductStorage.ConnectionString(Path.Combine(snapshot,"database.sqlite")) ).Options);
  Check((await restored.Users.SingleAsync()).SecurityStamp=="synthetic-stamp","保全DBから人物とstampを復元");
  var restoreServices=new ServiceCollection();restoreServices.AddDataProtection().SetApplicationName(AuthenticationPipeline.ProtectionApplicationName).PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(snapshot,"keys")));using var rp=restoreServices.BuildServiceProvider();
  using var op=services.BuildServiceProvider();var encrypted=op.GetRequiredService<IDataProtectionProvider>().CreateProtector("fixture").Protect("synthetic-roundtrip");
  Check(rp.GetRequiredService<IDataProtectionProvider>().CreateProtector("fixture").Unprotect(encrypted)=="synthetic-roundtrip","保全鍵で既存保護値を復元");
  var children=Directory.GetDirectories(backup).Length;
  await Rejected(async()=>{using var second=await ProductStorage.OpenAsync(settings);},"重複起動のlease拒否");Check(Directory.GetDirectories(backup).Length==children,"重複起動は保全処理も始めない");
 }
 using(var again=await ProductStorage.OpenAsync(settings))Check(true,"停止後のlease再取得");
 using(var wal=new SqliteConnection(ProductStorage.ConnectionString(dbPath))) {
  wal.Open();using var write=wal.CreateCommand();write.CommandText="PRAGMA journal_mode=WAL; UPDATE AspNetUsers SET DisplayName='WAL合成人物';";write.ExecuteNonQuery();
  using(var runtime=await ProductStorage.OpenAsync(settings)) {
   var marker=Directory.GetFiles(backup,"verified.json",SearchOption.AllDirectories).OrderDescending().First();
   await using var recovered=new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(ProductStorage.ConnectionString(Path.Combine(Path.GetDirectoryName(marker)!,"database.sqlite"))).Options);
   Check((await recovered.Users.SingleAsync()).DisplayName=="WAL合成人物","WAL内の確定データを保全・復元");
  }
 }
 using(var c=new SqliteConnection(ProductStorage.ConnectionString(dbPath))){c.Open();using var command=c.CreateCommand();command.CommandText="CREATE TABLE UnknownFixture (Id TEXT);";command.ExecuteNonQuery();}
 var unknown=Hash(dbPath);await Rejected(async()=>{using var runtime=await ProductStorage.OpenAsync(settings);},"未知schema拒否");Check(Hash(dbPath)==unknown,"未知schema拒否でも原DB不変");
 // 拒否後もleaseは必ず解放する。
 using(var lease=StorageLease.Acquire(data))Check(true,"失敗後のlease解放");
 var link=Path.Combine(root,"link-data");Directory.CreateSymbolicLink(link,data);
 await Rejected(async()=>{using var runtime=await ProductStorage.OpenAsync(settings with {Database=Path.Combine(link,"synthetic.sqlite")});},"symlink拒否");
 var badKey=Path.Combine(keys,"invalid.xml");File.WriteAllText(badKey,"<!DOCTYPE x [<!ENTITY e SYSTEM 'file:///etc/passwd'>]><key>&e;</key>");
 await Rejected(async()=>{using var runtime=await ProductStorage.OpenAsync(settings);},"鍵XML外部参照拒否");
 File.Delete(badKey);
 File.WriteAllText(badKey,"<key id='11111111-1111-1111-1111-111111111111' version='1'><creationDate>2026-01-01T00:00:00Z</creationDate><activationDate>2026-01-01T00:00:00Z</activationDate><expirationDate>2027-01-01T00:00:00Z</expirationDate></key>");
 await Rejected(async()=>{using var runtime=await ProductStorage.OpenAsync(settings);},"標準鍵ringの不正descriptor拒否");
 // 自動削除で既存の保全を失わず、起動失敗の無限コピーを抑止する。
 for(int i=Directory.GetFileSystemEntries(backup,"startup-*").Length;i<32;i++)Directory.CreateDirectory(Path.Combine(backup,"startup-synthetic-"+i));
 var count=Directory.GetFileSystemEntries(backup,"startup-*").Length;
 await Rejected(async()=>{using var runtime=await ProductStorage.OpenAsync(settings);},"保全数上限で停止");Check(Directory.GetFileSystemEntries(backup,"startup-*").Length==count,"保全上限で増加・自動削除なし");
 Console.WriteLine($"製品配置・保全 {checks}検査: 成功（合成DB／鍵のみ）");
} finally {SqliteConnection.ClearAllPools();Directory.Delete(root,true);}
