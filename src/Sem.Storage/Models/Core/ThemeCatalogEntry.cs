// 保全済み旧mainからモデル宣言のみを抽出。サービス・画面・起動処理は含めない。
using System.Text.Json.Serialization;
namespace Sem.Web.Core;

/// <summary>Stored catalog record. BuiltIn is managed only by migrations, never by the edit API.</summary>
public sealed class ThemeCatalogEntry
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Accent { get; set; } = "";
    public string Background { get; set; } = "";
    public string Surface { get; set; } = "";
    public string Text { get; set; } = "";
    public bool BuiltIn { get; set; }
    public long RowVersion { get; set; }
}
