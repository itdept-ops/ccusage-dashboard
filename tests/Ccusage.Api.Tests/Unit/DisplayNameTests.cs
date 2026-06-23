using Ccusage.Api.Data.Entities;
using Ccusage.Api.Services;
using FluentAssertions;

namespace Ccusage.Api.Tests.Unit;

/// <summary>
/// The central display-name formatter: how a user's stored full name + their own DisplayNameMode/Nickname
/// turn into the string OTHERS see. Default is FirstInitial ("First L."). Never an email.
/// </summary>
public class DisplayNameTests
{
    [Theory]
    [InlineData(DisplayNameMode.Full, "Jane Mary Smith")]
    [InlineData(DisplayNameMode.FirstName, "Jane")]
    [InlineData(DisplayNameMode.FirstInitial, "Jane S.")]
    public void Formats_each_mode_from_a_full_name(DisplayNameMode mode, string expected)
        => DisplayName.Format("Jane Mary Smith", mode, null).Should().Be(expected);

    [Fact]
    public void FirstInitial_is_the_default_mode_value()
        => new AppUser().DisplayNameMode.Should().Be(DisplayNameMode.FirstInitial);

    [Fact]
    public void FirstInitial_of_a_single_token_name_is_just_that_token()
        => DisplayName.Format("Cher", DisplayNameMode.FirstInitial, null).Should().Be("Cher");

    [Fact]
    public void Nickname_mode_uses_the_nickname_when_present()
        => DisplayName.Format("Jane Smith", DisplayNameMode.Nickname, "JJ").Should().Be("JJ");

    [Fact]
    public void Nickname_mode_falls_back_to_full_name_when_nickname_blank()
        => DisplayName.Format("Jane Smith", DisplayNameMode.Nickname, "   ").Should().Be("Jane Smith");

    [Fact]
    public void Blank_full_name_yields_unknown()
        => DisplayName.Format("   ", DisplayNameMode.Full, null).Should().Be(DisplayName.Unknown);

    [Theory]
    [InlineData(DisplayNameMode.Full)]
    [InlineData(DisplayNameMode.FirstName)]
    [InlineData(DisplayNameMode.FirstInitial)]
    public void Never_leaks_an_email_through_an_email_shaped_name(DisplayNameMode mode)
    {
        // An email-shaped "name" claim (the presence fallback path) is reduced to its local part — the
        // result must never contain an address.
        var result = DisplayName.Format("jane.smith@example.com", mode, null);
        result.Should().NotContain("@");
        result.Should().NotContain("example.com");
    }

    [Fact]
    public void Nickname_is_sanitized_of_at_signs_and_control_chars_and_capped()
    {
        DisplayName.SanitizeNickname("J@J\n  cool").Should().Be("JJ cool");
        DisplayName.SanitizeNickname("   ").Should().BeNull();
        DisplayName.SanitizeNickname(new string('x', 200))!.Length.Should().Be(DisplayName.MaxNicknameLength);
    }

    [Fact]
    public void Status_is_sanitized_masks_emails_and_capped()
    {
        DisplayName.SanitizeStatus("heads-down\tcoding").Should().Be("heads-down coding");
        DisplayName.SanitizeStatus("ping me@x.com please").Should().NotContain("@");
        DisplayName.SanitizeStatus("   ").Should().BeNull();
        DisplayName.SanitizeStatus(new string('y', 300))!.Length.Should().Be(DisplayName.MaxStatusLength);
    }

    [Theory]
    [InlineData("full", DisplayNameMode.Full)]
    [InlineData("FirstName", DisplayNameMode.FirstName)]
    [InlineData("firstInitial", DisplayNameMode.FirstInitial)]
    [InlineData("nickname", DisplayNameMode.Nickname)]
    public void Wire_token_round_trips(string wire, DisplayNameMode mode)
    {
        DisplayName.TryParseMode(wire, out var parsed).Should().BeTrue();
        parsed.Should().Be(mode);
        DisplayName.ModeToWire(mode).Should().Be(DisplayName.ModeToWire(parsed));
    }

    [Fact]
    public void Unknown_wire_token_is_rejected()
        => DisplayName.TryParseMode("sideways", out _).Should().BeFalse();
}
