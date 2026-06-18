using Ccusage.Api.Auth;
using Ccusage.Api.Data;
using Ccusage.Api.Data.Entities;
using Ccusage.Api.Dtos;
using Microsoft.EntityFrameworkCore;

namespace Ccusage.Api.Endpoints;

/// <summary>
/// Admin-managed, MUTUAL chat contacts ("circles"). A user's contacts are the people who appear in
/// their New-DM / channel-member picker. Contacts are symmetric: adding A→B also writes B→A, and
/// removing deletes both directions. Curating other people's circles is gated by
/// <see cref="Permissions.ChatContactsManage"/>; the caller's own list (for the picker) needs only
/// <see cref="Permissions.ChatRead"/>. All emails are compared/stored lower-cased; a self-contact is
/// never written. Identity comes from the JWT (.RequireAuthorization()).
/// </summary>
public static class ContactsEndpoints
{
    public static void MapContactsEndpoints(this WebApplication app)
    {
        var g = app.MapGroup("/api/chat").RequireAuthorization();

        // ---- The caller's own contacts (for the New-DM / member picker) ----
        g.MapGet("/contacts/me", async (CurrentUserAccessor me, UsageDbContext db, CancellationToken ct) =>
        {
            var user = (await me.GetUserAsync(ct))!; // chat.read filter guarantees non-null
            return Results.Ok(await ContactsForAsync(db, user.Email, ct));
        }).RequirePermission(Permissions.ChatRead);

        // ---- The full team directory (admin editor search + an admin's own picker) ----
        // Every enabled user except the caller; resolved to display identity. Admins are never boxed
        // in by their own (possibly empty) circle, so their picker can draw from here instead.
        g.MapGet("/directory", async (CurrentUserAccessor me, UsageDbContext db, CancellationToken ct) =>
        {
            var user = (await me.GetUserAsync(ct))!;
            var people = await db.Users.AsNoTracking()
                .Where(u => u.IsEnabled && u.Email != user.Email)
                .OrderBy(u => u.Name == "" ? u.Email : u.Name)
                .Select(u => new ChatContactDto
                {
                    Email = u.Email,
                    Name = string.IsNullOrEmpty(u.Name) ? u.Email : u.Name,
                    Picture = u.Picture,
                })
                .ToListAsync(ct);
            return Results.Ok(people);
        }).RequirePermission(Permissions.ChatContactsManage);

        // ---- A specific user's contacts (admin editor) ----
        g.MapGet("/contacts/user/{email}", async (string email, UsageDbContext db, CancellationToken ct) =>
        {
            var owner = (email ?? "").Trim().ToLowerInvariant();
            if (owner.Length == 0) return Results.BadRequest(new { message = "A user email is required." });
            if (!await db.Users.AsNoTracking().AnyAsync(u => u.Email == owner, ct))
                return Results.NotFound(new { message = "That user doesn't exist." });
            return Results.Ok(await ContactsForAsync(db, owner, ct));
        }).RequirePermission(Permissions.ChatContactsManage);

        // ---- Add a contact to a user's circle (MUTUAL; idempotent; self-add ignored) ----
        g.MapPost("/contacts/user/{email}", async (
            string email, AddContactRequest req, CurrentUserAccessor me, UsageDbContext db, CancellationToken ct) =>
        {
            var actor = (await me.GetUserAsync(ct))!;
            var owner = (email ?? "").Trim().ToLowerInvariant();
            var contact = (req.ContactEmail ?? "").Trim().ToLowerInvariant();
            if (owner.Length == 0 || contact.Length == 0)
                return Results.BadRequest(new { message = "Both the user and the contact email are required." });

            // Both ends must be existing, enabled users.
            var existing = await db.Users.AsNoTracking()
                .Where(u => u.IsEnabled && (u.Email == owner || u.Email == contact))
                .Select(u => u.Email)
                .ToListAsync(ct);
            if (!existing.Contains(owner))
                return Results.NotFound(new { message = "That user doesn't exist or is disabled." });
            // A self-contact is silently ignored (no-op), returning the unchanged list.
            if (owner != contact)
            {
                if (!existing.Contains(contact))
                    return Results.BadRequest(new { message = "That contact doesn't exist or is disabled." });
                await EnsurePairAsync(db, owner, contact, actor.Email, ct);
            }
            return Results.Ok(await ContactsForAsync(db, owner, ct));
        }).RequirePermission(Permissions.ChatContactsManage);

        // ---- Remove a contact from a user's circle (MUTUAL; no-op if absent) ----
        g.MapDelete("/contacts/user/{email}/{contactEmail}", async (
            string email, string contactEmail, UsageDbContext db, CancellationToken ct) =>
        {
            var owner = (email ?? "").Trim().ToLowerInvariant();
            var contact = (contactEmail ?? "").Trim().ToLowerInvariant();
            if (owner.Length == 0 || contact.Length == 0)
                return Results.BadRequest(new { message = "Both the user and the contact email are required." });
            if (!await db.Users.AsNoTracking().AnyAsync(u => u.Email == owner, ct))
                return Results.NotFound(new { message = "That user doesn't exist." });

            // Remove BOTH directions; absent rows are simply a no-op.
            await db.ChatContacts
                .Where(c => (c.OwnerEmail == owner && c.ContactEmail == contact)
                         || (c.OwnerEmail == contact && c.ContactEmail == owner))
                .ExecuteDeleteAsync(ct);
            return Results.Ok(await ContactsForAsync(db, owner, ct));
        }).RequirePermission(Permissions.ChatContactsManage);
    }

    /// <summary>
    /// Write both directions of a contact pair if missing. Idempotent: an existing pair is left as-is,
    /// and a concurrent insert that trips the unique index is swallowed (the pair already exists).
    /// </summary>
    private static async Task EnsurePairAsync(
        UsageDbContext db, string owner, string contact, string actorEmail, CancellationToken ct)
    {
        var present = await db.ChatContacts.AsNoTracking()
            .Where(c => (c.OwnerEmail == owner && c.ContactEmail == contact)
                     || (c.OwnerEmail == contact && c.ContactEmail == owner))
            .Select(c => new { c.OwnerEmail, c.ContactEmail })
            .ToListAsync(ct);
        var has = present.ToHashSet();

        var now = DateTime.UtcNow;
        if (!has.Contains(new { OwnerEmail = owner, ContactEmail = contact }))
            db.ChatContacts.Add(new ChatContact
            {
                OwnerEmail = owner, ContactEmail = contact, CreatedUtc = now, AddedByEmail = actorEmail,
            });
        if (!has.Contains(new { OwnerEmail = contact, ContactEmail = owner }))
            db.ChatContacts.Add(new ChatContact
            {
                OwnerEmail = contact, ContactEmail = owner, CreatedUtc = now, AddedByEmail = actorEmail,
            });

        if (db.ChangeTracker.HasChanges())
        {
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex) when (IsUniqueViolation(ex))
            {
                // A concurrent caller added the same pair between our read and save — the desired
                // state already exists, so treat it as a successful no-op.
                db.ChangeTracker.Clear();
            }
        }
    }

    /// <summary>Resolve a user's contacts to display identity (name/picture from AppUser), name-sorted.</summary>
    private static async Task<List<ChatContactDto>> ContactsForAsync(
        UsageDbContext db, string owner, CancellationToken ct) =>
        await db.ChatContacts.AsNoTracking()
            .Where(c => c.OwnerEmail == owner)
            .Join(db.Users.AsNoTracking(), c => c.ContactEmail, u => u.Email, (c, u) => u)
            .Where(u => u.IsEnabled)
            .OrderBy(u => u.Name == "" ? u.Email : u.Name)
            .Select(u => new ChatContactDto
            {
                Email = u.Email,
                Name = string.IsNullOrEmpty(u.Name) ? u.Email : u.Name,
                Picture = u.Picture,
            })
            .ToListAsync(ct);

    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is Npgsql.PostgresException pg && pg.SqlState == Npgsql.PostgresErrorCodes.UniqueViolation;
}
