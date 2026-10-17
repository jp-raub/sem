using System.Globalization;
namespace Sem.Web.Components.Shared;
public static class TimetableDisplay
{
    public static string Clock(DateTimeOffset value, string format, DateTimeOffset eventDate)
    {
        var local = value.ToOffset(TimeSpan.FromHours(9));
        var reference = eventDate.ToOffset(TimeSpan.FromHours(9));
        var pattern = format == "minutes" ? "HH:mm" : "HH:mm:ss";
        if (local.Date != reference.Date) pattern = "yyyy/MM/dd " + pattern;
        return local.ToString(pattern, CultureInfo.InvariantCulture);
    }
}
