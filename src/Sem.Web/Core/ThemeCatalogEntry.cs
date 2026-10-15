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

/// <summary>Safe catalog projection; contains no account or authentication data.</summary>
public sealed record ThemeCatalogEntryView(string Id, string Name, string Accent, string Background,
    string Surface, string Text, bool BuiltIn, long RowVersion);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ThemeCatalogEdit(string Name, string Accent, string Background, string Surface,
    string Text, long RowVersion);