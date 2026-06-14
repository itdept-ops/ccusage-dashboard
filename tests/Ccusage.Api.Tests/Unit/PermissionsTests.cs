using Ccusage.Api.Auth;
using FluentAssertions;

namespace Ccusage.Api.Tests.Unit;

public class PermissionsTests
{
    [Theory]
    [InlineData("dashboard.view")]
    [InlineData("sync.run")]
    [InlineData("pricing.manage")]
    [InlineData("settings.manage")]
    [InlineData("users.manage")]
    public void IsValid_is_true_for_each_known_key(string key)
    {
        Permissions.IsValid(key).Should().BeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("nope")]
    [InlineData("DASHBOARD.VIEW")]
    [InlineData("dashboard.viewx")]
    public void IsValid_is_false_for_unknown_or_wrong_case_key(string key)
    {
        Permissions.IsValid(key).Should().BeFalse();
    }

    [Fact]
    public void Constants_match_their_canonical_key_strings()
    {
        Permissions.DashboardView.Should().Be("dashboard.view");
        Permissions.SyncRun.Should().Be("sync.run");
        Permissions.PricingManage.Should().Be("pricing.manage");
        Permissions.SettingsManage.Should().Be("settings.manage");
        Permissions.UsersManage.Should().Be("users.manage");
    }

    [Fact]
    public void All_contains_exactly_the_five_known_keys()
    {
        Permissions.All.Should().HaveCount(5);
        Permissions.All.Should().Contain(new[]
        {
            "dashboard.view",
            "sync.run",
            "pricing.manage",
            "settings.manage",
            "users.manage",
        });
    }

    [Fact]
    public void All_has_no_duplicates()
    {
        Permissions.All.Distinct().Count().Should().Be(Permissions.All.Length);
    }

    [Fact]
    public void Catalog_has_five_entries()
    {
        Permissions.Catalog.Should().HaveCount(5);
    }

    [Fact]
    public void Every_catalog_key_passes_IsValid()
    {
        foreach (var info in Permissions.Catalog)
        {
            Permissions.IsValid(info.Key).Should().BeTrue();
        }
    }

    [Fact]
    public void Catalog_keys_equal_All_in_order()
    {
        Permissions.Catalog.Select(p => p.Key).Should().Equal(Permissions.All);
    }

    [Fact]
    public void Every_catalog_entry_has_non_empty_label_and_description()
    {
        foreach (var info in Permissions.Catalog)
        {
            info.Label.Should().NotBeNullOrWhiteSpace();
            info.Description.Should().NotBeNullOrWhiteSpace();
        }
    }
}
