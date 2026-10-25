using Sem.Web.Core;
namespace Sem.Web.Components.Shared;
// Reorders only the caller-owned UI draft; persistence/authorization stay in Core.
public static class DefinitionDraftOrder
{
    public static bool MoveTo(IList<PartDefinitionView> rows, string source, string target)
    {
        var from = Index(rows, source); var to = Index(rows, target);
        if (from < 0 || to < 0 || from == to) return false;
        var item = rows[from]; rows.RemoveAt(from); rows.Insert(to, item); return true;
    }
    public static bool Step(IList<PartDefinitionView> rows, string id, int delta)
    {
        if (delta is not (-1 or 1)) return false;
        var from = Index(rows, id); var to = from + delta;
        return from >= 0 && to >= 0 && to < rows.Count && MoveTo(rows, id, rows[to].Id);
    }
    static int Index(IList<PartDefinitionView> rows, string id)
    {
        if (string.IsNullOrEmpty(id)) return -1;
        for (var i = 0; i < rows.Count; i++) if (rows[i].Id == id) return i;
        return -1;
    }
}
