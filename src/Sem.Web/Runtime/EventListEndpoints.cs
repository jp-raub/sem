using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Sem.Web.Data;
using Sem.Authentication;
namespace Sem.Web.Runtime;
public static class EventListEndpoints
{
    public static void MapSemEventList(this WebApplication app)
    {
        app.MapGet("/api/ui/events", async (HttpContext context, ApplicationDbContext db, IOptions<IdentityOptions> identity, TimeProvider clock) => {
            if (context.Request.QueryString.HasValue) return Results.BadRequest();
            var value = await EventListReader.ReadAsync(db, context.User, identity.Value, clock.GetUtcNow(), context.RequestAborted);
            return value is null ? Results.Unauthorized() : Results.Json(value);
        }).RequireRateLimiting(AuthenticationPipeline.LoginPolicy);
    }
}
