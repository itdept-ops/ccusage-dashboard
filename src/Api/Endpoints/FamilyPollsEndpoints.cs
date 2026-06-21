using Ccusage.Api.Auth;
using Ccusage.Api.Data;
using Ccusage.Api.Data.Entities;
using Ccusage.Api.Services;
using Microsoft.EntityFrameworkCore;
using static Ccusage.Api.Services.GoogleCalendarService;

namespace Ccusage.Api.Endpoints;

/// <summary>
/// Family Hub F6b — DOODLE-STYLE PLAN POLLS (/api/family/polls), gated by <see cref="Permissions.FamilyUse"/>
/// on top of <c>.RequireAuthorization()</c>. A poll is a group decision owned by the caller's HOUSEHOLD; its
/// options are either candidate TIME slots (start/end) or free-text labels. Members vote for EVERY option
/// that works for them (re-voting replaces their prior votes for that poll), and the poll can be closed with
/// a winner (defaulting to the most-voted). A TIME option can then be BOOKED onto the caller's connected
/// calendar (reusing <see cref="GoogleCalendarService.CreateEventAsync"/>).
///
/// PRIVACY (enforced server-side): a caller only ever addresses their OWN household; a cross-household poll
/// id is a 404 (existence never leaked). Voters/creators are exposed by AppUser id + display name ONLY — an
/// email is NEVER on the wire. Booking degrades gracefully when the caller has no connected calendar (400),
/// never a 500.
/// </summary>
public static class FamilyPollsEndpoints
{
    // ---- Request DTOs ----
    public sealed record OptionInput(DateTime? StartUtc, DateTime? EndUtc, string? Label);
    public sealed record CreatePollRequest(string? Title, string? Kind, IReadOnlyList<OptionInput>? Options);
    public sealed record VoteRequest(long[]? OptionIds);
    public sealed record ClosePollRequest(long? WinningOptionId);
    public sealed record BookRequest(long OptionId);

    // ---- Response DTOs (people by userId + name; never email) ----
    public sealed record VoterDto(int UserId, string Name);
    public sealed record PollOptionDto(
        long Id, DateTime? StartUtc, DateTime? EndUtc, string? Label, int SortOrder,
        int VoteCount, IReadOnlyList<VoterDto> Voters);
    public sealed record PollDto(
        long Id, string Title, string Kind, bool Closed, long? WinningOptionId,
        int CreatedByUserId, string CreatedByName, DateTime CreatedUtc,
        IReadOnlyList<PollOptionDto> Options, IReadOnlyList<long> MyVotes);

    private static readonly string[] Kinds = { "time", "text" };

    public static void MapFamilyPollsEndpoints(this WebApplication app)
    {
        var g = app.MapGroup("/api/family/polls")
            .RequireAuthorization()
            .RequirePermission(Permissions.FamilyUse);

        // ---- GET /api/family/polls : the household's polls (newest first) ----
        g.MapGet("/", async (
            CurrentUserAccessor me, CurrentHouseholdAccessor households, UsageDbContext db, CancellationToken ct) =>
        {
            var caller = (await me.GetUserAsync(ct))!;
            var household = (await households.GetOrCreateForCallerAsync(caller, ct))!;

            var polls = await db.FamilyPlanPolls.AsNoTracking()
                .Where(p => p.HouseholdId == household.Id)
                .OrderByDescending(p => p.Id)
                .ToListAsync(ct);

            return Results.Ok(await BuildPollDtosAsync(db, polls, caller.Id, ct));
        });

        // ---- POST /api/family/polls : create a time/text poll with its options ----
        g.MapPost("/", async (
            CreatePollRequest req, CurrentUserAccessor me, CurrentHouseholdAccessor households,
            UsageDbContext db, CancellationToken ct) =>
        {
            var caller = (await me.GetUserAsync(ct))!;
            var household = (await households.GetOrCreateForCallerAsync(caller, ct))!;

            var title = (req.Title ?? "").Trim();
            if (string.IsNullOrEmpty(title)) return Results.BadRequest(new { message = "A poll title is required." });
            if (title.Length > 200) title = title[..200];

            var kind = (req.Kind ?? "time").Trim().ToLowerInvariant();
            if (!Kinds.Contains(kind))
                return Results.BadRequest(new { message = "Poll kind must be \"time\" or \"text\"." });

            var inputs = req.Options ?? Array.Empty<OptionInput>();
            if (inputs.Count < 2)
                return Results.BadRequest(new { message = "A poll needs at least two options." });
            if (inputs.Count > 30)
                return Results.BadRequest(new { message = "A poll can have at most 30 options." });

            var options = new List<FamilyPlanPollOption>(inputs.Count);
            var sort = 0;
            foreach (var input in inputs)
            {
                if (kind == "time")
                {
                    if (input.StartUtc is not DateTime s || input.EndUtc is not DateTime en)
                        return Results.BadRequest(new { message = "Each time option needs a startUtc and endUtc." });
                    var start = DateTime.SpecifyKind(s, DateTimeKind.Utc);
                    var end = DateTime.SpecifyKind(en, DateTimeKind.Utc);
                    if (end <= start)
                        return Results.BadRequest(new { message = "A time option's end must be after its start." });
                    options.Add(new FamilyPlanPollOption { StartUtc = start, EndUtc = end, SortOrder = sort++ });
                }
                else
                {
                    var label = (input.Label ?? "").Trim();
                    if (label.Length == 0)
                        return Results.BadRequest(new { message = "Each text option needs a label." });
                    if (label.Length > 200) label = label[..200];
                    options.Add(new FamilyPlanPollOption { Label = label, SortOrder = sort++ });
                }
            }

            var poll = new FamilyPlanPoll
            {
                HouseholdId = household.Id,
                CreatedByUserId = caller.Id,
                Title = title,
                Kind = kind,
                Closed = false,
                CreatedUtc = DateTime.UtcNow,
                Options = options,
            };
            db.FamilyPlanPolls.Add(poll);
            await db.SaveChangesAsync(ct);

            return Results.Ok(await BuildPollDtoAsync(db, poll.Id, caller.Id, ct));
        });

        // ---- POST /api/family/polls/{id}/vote : replace the caller's votes for this poll ----
        g.MapPost("/{id:long}/vote", async (
            long id, VoteRequest req, CurrentUserAccessor me, CurrentHouseholdAccessor households,
            UsageDbContext db, CancellationToken ct) =>
        {
            var caller = (await me.GetUserAsync(ct))!;
            var household = (await households.GetOrCreateForCallerAsync(caller, ct))!;

            var poll = await db.FamilyPlanPolls.Include(p => p.Options)
                .FirstOrDefaultAsync(p => p.Id == id, ct);
            if (poll is null || poll.HouseholdId != household.Id) return NotFound();
            if (poll.Closed) return Results.BadRequest(new { message = "This poll is closed." });

            // Keep only ids that are actual options on THIS poll (ignore strays); de-dup.
            var validOptionIds = poll.Options.Select(o => o.Id).ToHashSet();
            var chosen = (req.OptionIds ?? Array.Empty<long>()).Distinct().Where(validOptionIds.Contains).ToList();

            // Replace: clear the caller's prior votes for this poll's options, then add the new set.
            var optionIds = poll.Options.Select(o => o.Id).ToList();
            await db.FamilyPlanPollVotes
                .Where(v => optionIds.Contains(v.OptionId) && v.UserId == caller.Id)
                .ExecuteDeleteAsync(ct);

            var now = DateTime.UtcNow;
            foreach (var optionId in chosen)
                db.FamilyPlanPollVotes.Add(new FamilyPlanPollVote
                {
                    OptionId = optionId, UserId = caller.Id, CreatedUtc = now,
                });
            await db.SaveChangesAsync(ct);

            return Results.Ok(await BuildPollDtoAsync(db, poll.Id, caller.Id, ct));
        });

        // ---- POST /api/family/polls/{id}/close : close the poll, picking a winner (default most-voted) ----
        g.MapPost("/{id:long}/close", async (
            long id, ClosePollRequest req, CurrentUserAccessor me, CurrentHouseholdAccessor households,
            UsageDbContext db, CancellationToken ct) =>
        {
            var caller = (await me.GetUserAsync(ct))!;
            var household = (await households.GetOrCreateForCallerAsync(caller, ct))!;

            var poll = await db.FamilyPlanPolls.Include(p => p.Options)
                .FirstOrDefaultAsync(p => p.Id == id, ct);
            if (poll is null || poll.HouseholdId != household.Id) return NotFound();

            long? winner;
            if (req.WinningOptionId is long explicitWinner)
            {
                if (poll.Options.All(o => o.Id != explicitWinner))
                    return Results.BadRequest(new { message = "That winning option isn't on this poll." });
                winner = explicitWinner;
            }
            else
            {
                winner = await MostVotedOptionIdAsync(db, poll, ct);
            }

            poll.Closed = true;
            poll.WinningOptionId = winner;
            await db.SaveChangesAsync(ct);

            return Results.Ok(await BuildPollDtoAsync(db, poll.Id, caller.Id, ct));
        });

        // ---- POST /api/family/polls/{id}/book : book a TIME option onto the caller's calendar ----
        g.MapPost("/{id:long}/book", async (
            long id, BookRequest req, CurrentUserAccessor me, CurrentHouseholdAccessor households,
            UsageDbContext db, GoogleCalendarService cal, CancellationToken ct) =>
        {
            var caller = (await me.GetUserAsync(ct))!;
            var household = (await households.GetOrCreateForCallerAsync(caller, ct))!;

            var poll = await db.FamilyPlanPolls.Include(p => p.Options)
                .FirstOrDefaultAsync(p => p.Id == id, ct);
            if (poll is null || poll.HouseholdId != household.Id) return NotFound();

            var option = poll.Options.FirstOrDefault(o => o.Id == req.OptionId);
            if (option is null) return NotFound();

            // Only TIME options (with a real slot) can be booked onto a calendar.
            if (poll.Kind != "time" || option.StartUtc is not DateTime start || option.EndUtc is not DateTime end)
                return Results.BadRequest(new { message = "Only a time option can be booked onto a calendar." });

            var result = await cal.CreateEventAsync(
                caller.Id, poll.Title, DateTime.SpecifyKind(start, DateTimeKind.Utc),
                DateTime.SpecifyKind(end, DateTimeKind.Utc), allDay: false,
                location: null, description: "Booked from a family plan poll.", ct: ct);

            // Graceful: an unconnected/unconfigured caller gets a clear 400, never a 500.
            if (!result.Ok) return result.Status switch
            {
                CalendarStatus.NotConnected => Results.BadRequest(new
                {
                    message = "Connect your Google Calendar to book this time.", connected = false,
                }),
                CalendarStatus.NotConfigured => Results.BadRequest(new
                {
                    message = "Google Calendar isn't configured on this server.", configured = false,
                }),
                _ => Results.Json(new { message = "Google Calendar is temporarily unavailable. Please try again." },
                    statusCode: StatusCodes.Status502BadGateway),
            };

            var e = result.Value!;
            return Results.Ok(new EventDtoLite(e.Id, e.Title, e.StartUtc, e.EndUtc, e.HtmlLink));
        });

        // ---- DELETE /api/family/polls/{id} ----
        g.MapDelete("/{id:long}", async (
            long id, CurrentUserAccessor me, CurrentHouseholdAccessor households,
            UsageDbContext db, CancellationToken ct) =>
        {
            var caller = (await me.GetUserAsync(ct))!;
            var household = (await households.GetOrCreateForCallerAsync(caller, ct))!;

            var poll = await db.FamilyPlanPolls.FirstOrDefaultAsync(p => p.Id == id, ct);
            if (poll is null || poll.HouseholdId != household.Id) return NotFound();

            db.FamilyPlanPolls.Remove(poll); // options + votes cascade
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });
    }

    /// <summary>A slim booked-event payload (mirrors the calendar event, sans Google internals).</summary>
    public sealed record EventDtoLite(string Id, string Title, DateTime? StartUtc, DateTime? EndUtc, string? HtmlLink);

    // =====================================================================================
    // DTO assembly — per-option vote counts + voter names, my votes, winner (no email)
    // =====================================================================================

    /// <summary>The option id with the most votes on <paramref name="poll"/>; null when no votes exist. Ties
    /// resolve to the earliest option (lowest SortOrder, then id) so the default winner is deterministic.</summary>
    private static async Task<long?> MostVotedOptionIdAsync(UsageDbContext db, FamilyPlanPoll poll, CancellationToken ct)
    {
        var optionIds = poll.Options.Select(o => o.Id).ToList();
        if (optionIds.Count == 0) return null;

        var counts = await db.FamilyPlanPollVotes.AsNoTracking()
            .Where(v => optionIds.Contains(v.OptionId))
            .GroupBy(v => v.OptionId)
            .Select(grp => new { OptionId = grp.Key, Count = grp.Count() })
            .ToListAsync(ct);
        if (counts.Count == 0) return null;

        var max = counts.Max(c => c.Count);
        var topIds = counts.Where(c => c.Count == max).Select(c => c.OptionId).ToHashSet();
        // Break ties deterministically by the option's display order.
        return poll.Options
            .Where(o => topIds.Contains(o.Id))
            .OrderBy(o => o.SortOrder).ThenBy(o => o.Id)
            .Select(o => (long?)o.Id)
            .FirstOrDefault();
    }

    private static async Task<PollDto> BuildPollDtoAsync(UsageDbContext db, long pollId, int callerId, CancellationToken ct)
    {
        var poll = await db.FamilyPlanPolls.AsNoTracking().FirstAsync(p => p.Id == pollId, ct);
        return (await BuildPollDtosAsync(db, new[] { poll }, callerId, ct))[0];
    }

    /// <summary>
    /// Build the wire DTOs for a set of polls in a few batched queries: load their options, tally votes per
    /// option, resolve voter + creator display names (NEVER email), and mark the caller's own votes.
    /// </summary>
    private static async Task<IReadOnlyList<PollDto>> BuildPollDtosAsync(
        UsageDbContext db, IReadOnlyList<FamilyPlanPoll> polls, int callerId, CancellationToken ct)
    {
        if (polls.Count == 0) return Array.Empty<PollDto>();

        var pollIds = polls.Select(p => p.Id).ToList();

        var options = await db.FamilyPlanPollOptions.AsNoTracking()
            .Where(o => pollIds.Contains(o.PollId))
            .ToListAsync(ct);
        var optionsByPoll = options.GroupBy(o => o.PollId).ToDictionary(grp => grp.Key, grp => grp.ToList());
        var optionIds = options.Select(o => o.Id).ToList();

        var votes = optionIds.Count == 0
            ? new List<FamilyPlanPollVote>()
            : await db.FamilyPlanPollVotes.AsNoTracking()
                .Where(v => optionIds.Contains(v.OptionId))
                .ToListAsync(ct);
        var votesByOption = votes.GroupBy(v => v.OptionId).ToDictionary(grp => grp.Key, grp => grp.ToList());

        // Resolve every voter + every poll creator to a display name in one query (never email).
        var personIds = votes.Select(v => v.UserId)
            .Concat(polls.Select(p => p.CreatedByUserId))
            .Distinct().ToList();
        var names = await NamesAsync(db, personIds, ct);

        var result = new List<PollDto>(polls.Count);
        foreach (var poll in polls)
        {
            var pollOptions = (optionsByPoll.GetValueOrDefault(poll.Id) ?? new())
                .OrderBy(o => o.SortOrder).ThenBy(o => o.Id)
                .ToList();

            var optionDtos = new List<PollOptionDto>(pollOptions.Count);
            var myVotes = new List<long>();
            foreach (var o in pollOptions)
            {
                var optionVotes = votesByOption.GetValueOrDefault(o.Id) ?? new();
                var voters = optionVotes
                    .OrderBy(v => v.CreatedUtc).ThenBy(v => v.Id)
                    .Select(v => new VoterDto(v.UserId, Name(names, v.UserId)))
                    .ToList();
                if (optionVotes.Any(v => v.UserId == callerId)) myVotes.Add(o.Id);

                optionDtos.Add(new PollOptionDto(
                    o.Id, o.StartUtc, o.EndUtc, o.Label, o.SortOrder, optionVotes.Count, voters));
            }

            result.Add(new PollDto(
                poll.Id, poll.Title, poll.Kind, poll.Closed, poll.WinningOptionId,
                poll.CreatedByUserId, Name(names, poll.CreatedByUserId), poll.CreatedUtc,
                optionDtos, myVotes));
        }
        return result;
    }

    // =====================================================================================
    // Helpers
    // =====================================================================================

    private static async Task<Dictionary<int, string>> NamesAsync(
        UsageDbContext db, IEnumerable<int> userIds, CancellationToken ct)
    {
        var ids = userIds.Distinct().ToList();
        if (ids.Count == 0) return new Dictionary<int, string>();
        return await db.Users.AsNoTracking()
            .Where(u => ids.Contains(u.Id))
            .ToDictionaryAsync(
                u => u.Id,
                u => string.IsNullOrEmpty(u.Name) ? "Unknown user" : u.Name, ct);
    }

    private static string Name(Dictionary<int, string> names, int userId) =>
        names.TryGetValue(userId, out var n) ? n : "Unknown user";

    private static IResult NotFound() =>
        Results.NotFound(new { message = "That poll doesn't exist." });
}
