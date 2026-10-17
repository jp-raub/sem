using System.Globalization;
namespace Sem.Web.Components.Shared;

// Presentation only. Never mutate a collection's persistence order or pagination cursor.
public static class DisplayOrdering
{
    static readonly StringComparer JapaneseNames = StringComparer.Create(CultureInfo.GetCultureInfo("ja-JP"), false);
    public static IOrderedEnumerable<T> Names<T>(IEnumerable<T> rows, Func<T, string> name, Func<T, string> id, bool descending = false)
        => (descending ? rows.OrderByDescending(name, JapaneseNames) : rows.OrderBy(name, JapaneseNames)).ThenBy(id, StringComparer.Ordinal);
    public static IOrderedEnumerable<T> Dates<T>(IEnumerable<T> rows, Func<T, string> date, Func<T, string> id, bool descending = true)
        => (descending ? rows.OrderByDescending(x => DateTimeOffset.Parse(date(x), CultureInfo.InvariantCulture)) : rows.OrderBy(x => DateTimeOffset.Parse(date(x), CultureInfo.InvariantCulture))).ThenBy(id, StringComparer.Ordinal);
}
