namespace Ccusage.Api.Data.Entities;

/// <summary>
/// How a user's name is shown TO OTHER USERS. The user owns this choice — it governs how they appear
/// everywhere a name reaches another person. Stored as an int; never widen ordinals out from under
/// existing rows. The default for new/unset rows is <see cref="FirstInitial"/>.
/// </summary>
public enum DisplayNameMode
{
    /// <summary>Show the full name (e.g. "Jane Smith").</summary>
    Full = 0,

    /// <summary>Show only the first name token (e.g. "Jane").</summary>
    FirstName = 1,

    /// <summary>Show the first name plus the last name's initial (e.g. "Jane S."). The default.</summary>
    FirstInitial = 2,

    /// <summary>Show the user's chosen nickname (falling back to the formatted full name when blank).</summary>
    Nickname = 3,
}
