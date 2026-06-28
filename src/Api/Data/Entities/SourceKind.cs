namespace Ccusage.Api.Data.Entities;

/// <summary>
/// PROGRAM-2 #1 — how a tracker row originated. <see cref="Manual"/> rows were typed by the user;
/// <see cref="Watch"/> rows were auto-imported from a connected wearable (Fitbit) by the health sync.
///
/// INVARIANT: a wearable re-sync only ever upserts/overwrites <see cref="Watch"/> rows — it must NEVER
/// touch a <see cref="Manual"/> row. The default is <see cref="Manual"/> so every existing/typed row stays
/// owner-authored.
/// </summary>
public enum SourceKind
{
    Manual = 0,
    Watch = 1,
}
