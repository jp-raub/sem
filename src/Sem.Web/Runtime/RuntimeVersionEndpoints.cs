using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Sem.Web.Data;

namespace Sem.Web.Runtime;

public static class RuntimeVersionEndpoints
{
    public static IEndpointRouteBuilder MapRuntimeVersion(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/version", (HttpContext context) =>
        {
            if (!TryProbe(context, out var probe)) return Results.BadRequest();
            return Results.Json(RuntimeBuildInfo.Current.Payload(probe));
        }).AllowAnonymous();
        app.MapGet("/health/ready", async (HttpContext context, ApplicationDbContext db) =>
        {
            if (!TryProbe(context, out var probe)) return Results.BadRequest();
            return await db.Database.CanConnectAsync()
                ? Results.Json(new { status = "storage-ready", probe }) : Results.StatusCode(503);
        }).AllowAnonymous();
        return app;
    }

    public static bool TryProbe(HttpContext context, out string? probe)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.Pragma = "no-cache";
        probe = null;
        if (context.Request.Query.Keys.Any(k => k != "probe")) return false;
        if (!context.Request.Query.TryGetValue("probe", out var values)) return true;
        if (values.Count != 1 || values[0] is not string value ||
            !Regex.IsMatch(value, "\\A[0-9a-f]{32}\\z", RegexOptions.CultureInvariant)) return false;
        probe = value;
        return true;
    }
}
