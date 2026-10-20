using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Sem.Web.Core;
using Sem.Web.Data;

static class AtomicQuotaChecks
{
    public static async Task Run(IDbContextFactory<ApplicationDbContext> factory,
        CoreFoundationService core, ClaimsPrincipal admin, ClaimsPrincipal member,
        DateTimeOffset now, Action<bool, string> check)
    {
        var id = await core.SaveEventAsync(admin, null,
            new("Atomic quota fixture", "", "", now.AddDays(2), null, "", "Hidden", [], 0));
        var phases = await core.ReadPhasePlanAsync(admin, id);
        var custom = Guid.NewGuid().ToString();
        await core.SavePhasePlanAsync(admin, id,
            new(phases.RowVersion, true, custom, [new(custom, "Two songs", 2, null, null)]));
        var subject = member.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var question = Guid.NewGuid().ToString();
        var voice = Guid.NewGuid().ToString();
        var guitar = Guid.NewGuid().ToString();
        var songs = Enumerable.Range(0, 3).Select(_ => Guid.NewGuid().ToString()).ToArray();
        var slots = new Dictionary<string, string[]>();
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.EventPartDefinitions.AddRange(
                new() { Id = voice, EventId = id, Name = "Voice", NormalizedName = "VOICE", Category = "Sing" },
                new() { Id = guitar, EventId = id, Name = "Guitar", NormalizedName = "GUITAR", Category = "Play" });
            db.EventQuestions.Add(new() { Id = question, EventId = id, Text = "Retained answer", Type = "Text" });
            foreach (var song in songs)
            {
                db.Songs.Add(new() { Id = song, EventId = id, Title = "Quota song", PerformanceSeconds = 60 });
                slots[song] = [Guid.NewGuid().ToString(), Guid.NewGuid().ToString()];
                db.SongParts.AddRange(
                    new() { Id = slots[song][0], EventId = id, SongId = song, PartDefinitionId = voice },
                    new() { Id = slots[song][1], EventId = id, SongId = song, PartDefinitionId = guitar });
            }
            await db.SaveChangesAsync();
        }
        async Task<string> Snapshot()
        {
            await using var db = await factory.CreateDbContextAsync();
            return JsonSerializer.Serialize(new
            {
                Event = await db.SessionEvents.AsNoTracking().SingleAsync(x => x.Id == id),
                Slots = await db.SongParts.AsNoTracking().Where(x => x.EventId == id).OrderBy(x => x.Id).ToArrayAsync(),
                Answers = await db.EventQuestionAnswers.AsNoTracking().Where(x => x.QuestionId == question).OrderBy(x => x.UserId).ToArrayAsync(),
                Participants = await db.EventParticipantStates.AsNoTracking().Where(x => x.EventId == id).OrderBy(x => x.UserId).ToArrayAsync(),
                Spectators = await db.EventSpectators.AsNoTracking().Where(x => x.EventId == id).OrderBy(x => x.UserId).ToArrayAsync(),
                Operations = await db.RegistrationOperations.CountAsync(),
                Comments = await db.EventComments.CountAsync(),
                CommentOperations = await db.CommentOperations.CountAsync(),
                Notifications = await db.BusinessNotifications.CountAsync(),
                Audits = await db.Audits.CountAsync()
            });
        }
        async Task<ParticipationEdit> Draft(ClaimsPrincipal actor, string[] selected, bool proxy = false)
        {
            var plan = await core.ReadParticipationAsync(actor, id, subjectId: proxy ? subject : null);
            return new(Guid.NewGuid().ToString(), plan.RowVersion, plan.PhaseId, "Register",
                selected.Select(slot => new SlotChoice(slot, plan.Slots.Single(x => x.Id == slot).RowVersion)).ToArray(),
                [new(question, "must not partially save")], proxy ? subject : null, proxy,
                proxy ? "Test manager exception" : "", plan.ParticipantVersion, "Quota test comment");
        }
        async Task Reject(ClaimsPrincipal actor, ParticipationEdit edit, string label)
        {
            var before = await Snapshot();
            var denied = false;
            try { await core.SubmitParticipationAsync(actor, id, edit); }
            catch (ValidationException error) { denied = error.Message.Contains("曲数上限"); }
            check(denied, label);
            check(await Snapshot() == before, label + ": no slots, versions, answers, spectator, comment, notification, operation or audit changes");
        }
        var batch = await Draft(member, songs.Select(song => slots[song][0]).ToArray());
        await Reject(member, batch, "Two-song quota rejects a three-song batch atomically");
        await Reject(member, batch, "Rejected request retry cannot register a prefix");
        await Reject(member, batch with { Slots = batch.Slots.Reverse().ToArray(), RequestId = Guid.NewGuid().ToString() },
            "Quota rejection is independent of selection order");
        await Reject(admin, await Draft(admin, songs.Select(song => slots[song][0]).ToArray(), true),
            "Manager proxy and compatibility exception cannot bypass atomic quota");

        // Same-song voice+instrument plus another song is two songs, not three.
        var accepted = await core.SubmitParticipationAsync(member, id,
            await Draft(member, [slots[songs[0]][0], slots[songs[0]][1], slots[songs[1]][0]]));
        check(accepted.Items.Count(x => x.Success && x.Code == "Registered") == 3,
            "Three parts across two distinct songs remain within the two-song quota");
        var replayDraft = await Draft(member, [slots[songs[2]][0]]);
        await Reject(member, replayDraft, "Existing current-phase songs count toward the whole-batch limit");
        var after = await core.ReadParticipationAsync(member, id);
        check(after.OwnPhaseSongCount == 2 && after.Questions.Single().OwnValue == "must not partially save",
            "Rejected later batch preserves the previously successful songs and answer");
    }
}