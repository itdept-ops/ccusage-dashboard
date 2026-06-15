using Ccusage.Api.Services;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace Ccusage.Api.Tests.Unit;

public class TokenProtectorTests
{
    private static TokenProtector Build() => new(new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["Jwt:Key"] = "test-signing-key-at-least-32-bytes-long!!" })
        .Build());

    [Fact]
    public void Round_trips_a_token()
    {
        var p = Build();
        var token = "abc123_-XYZ";
        p.Unprotect(p.Protect(token)).Should().Be(token);
    }

    [Fact]
    public void Ciphertext_differs_each_time_but_decrypts_the_same()
    {
        var p = Build();
        var a = p.Protect("same");
        var b = p.Protect("same");
        a.Should().NotBe(b);                 // random nonce
        p.Unprotect(a).Should().Be("same");
        p.Unprotect(b).Should().Be("same");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-base64!!")]
    [InlineData("YWJj")] // valid base64 but too short / not a real blob
    public void Returns_null_for_missing_or_tampered(string? blob)
        => Build().Unprotect(blob).Should().BeNull();

    [Fact]
    public void A_different_key_cannot_decrypt()
    {
        var blob = Build().Protect("secret");
        var other = new TokenProtector(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Jwt:Key"] = "a-completely-different-key-32-bytes-min!!" })
            .Build());
        other.Unprotect(blob).Should().BeNull();
    }
}
