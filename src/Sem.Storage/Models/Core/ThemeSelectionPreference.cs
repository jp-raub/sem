// 保全済み旧mainからモデル宣言のみを抽出。サービス・画面・起動処理は含めない。
using System.Text.Json.Serialization;
namespace Sem.Web.Core;

/// <summary>Optional personal appearance layer owned only by the referenced account.</summary>
public sealed class UserThemePreference
{
    public string UserId { get; set; } = "";
    public string? ThemeId { get; set; }
    public int? Mode { get; set; }
    public long RowVersion { get; set; }
}


/// <summary>Optional appearance layer for one session event.</summary>
public sealed class EventThemePreference
{
    public string EventId { get; set; } = "";
    public string? ThemeId { get; set; }
    public int? Mode { get; set; }
    public long RowVersion { get; set; }
}


/// <summary>Optional site-wide appearance layer; only the row with Id 1 is valid.</summary>
public sealed class SiteThemePreference
{
    public int Id { get; set; } = 1;
    public string? ThemeId { get; set; }
    public int? Mode { get; set; }
    public long RowVersion { get; set; }
}
