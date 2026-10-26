using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using System.Text.Json;
if(args.Length!=2)throw new ArgumentException("保全ソースのsrc/Sem.Webと出力先を指定してください。実DBは使用しません。");
var input=args[0];var output=args[1];
var context=File.ReadAllText(Path.Combine(input,"Data/ApplicationDbContext.cs"));
var types=Regex.Matches(context,@"(?:DbSet|Entity|HasOne|HasMany)<(\w+)>").Select(m=>m.Groups[1].Value).Append("ApplicationUser").ToHashSet();
var entries=new List<object>();
foreach(var file in Directory.GetFiles(input,"*.cs",SearchOption.AllDirectories).Order(StringComparer.Ordinal).Where(p=>!p.Contains("/Migrations/")&&!p.Contains("/Components/"))){
 var original=File.ReadAllText(file);var tree=CSharpSyntaxTree.ParseText(original).GetCompilationUnitRoot();
 var models=tree.DescendantNodes().OfType<ClassDeclarationSyntax>().Where(c=>types.Contains(c.Identifier.Text)).ToArray();
 if(models.Length==0)continue;
 var ns=tree.DescendantNodes().OfType<BaseNamespaceDeclarationSyntax>().First().Name.ToString();
 var relative=Path.GetRelativePath(input,file);
 var dest=Path.Combine(output,"Models",relative);Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
 File.WriteAllText(dest,"// 保全済み旧mainからモデル宣言のみを抽出。サービス・画面・起動処理は含めない。\n"+string.Join("\n",tree.Usings.Where(x=>x.Name?.ToString()!="WebPush").Select(x=>x.ToFullString().Trim()))+"\nnamespace "+ns+";\n"+string.Join("\n",models.Select(m=>m.ToFullString())));
 entries.Add(new{source=relative,sha256=Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(file))),models=models.Select(c=>c.Identifier.Text).ToArray(),output=Path.GetRelativePath(output,dest),outputSha256=Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(dest)))});
}
Directory.CreateDirectory(output);
var unchangedFiles=new List<object>();
var direct=Directory.GetFiles(Path.Combine(input,"Data/Migrations"),"*.cs").Concat(new[]{Path.Combine(input,"Data/ApplicationDbContext.cs"),Path.Combine(input,"Data/SqliteStorageInitializer.cs")}).Order(StringComparer.Ordinal).ToArray();
foreach(var file in direct){var relative=Path.GetRelativePath(input,file);var dest=Path.Combine(output,relative);Directory.CreateDirectory(Path.GetDirectoryName(dest)!);File.Copy(file,dest,true);unchangedFiles.Add(new{path=relative,source=relative,sha256=Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(file)))});}
var migrationIds=direct.SelectMany(file=>Regex.Matches(File.ReadAllText(file),"\\[Migration\\(\"([^\"]+)\"\\)\\]").Select(m=>m.Groups[1].Value)).Order(StringComparer.Ordinal).ToArray();
File.WriteAllText(Path.Combine(output,"model-provenance.json"),JsonSerializer.Serialize(new{commit="812b2784212e9015ecbbce31280e48cb0bc65e84",entries,unchangedFiles,migrationIds},new JsonSerializerOptions{WriteIndented=true})+"\n");
Console.WriteLine($"{types.Count} モデルの依存元 {entries.Count} ファイルを抽出");
