using Ccusage.Api.Auth;
using FluentAssertions;

namespace Ccusage.Api.Tests.Unit;

public class PermissionsTests
{
    // The full catalog of 29 keys.
    private static readonly string[] AllKeys =
    {
        "dashboard.view", "dashboard.export", "sync.run",
        "calendar.view",
        "pricing.view", "pricing.manage",
        "settings.view", "settings.manage", "sources.manage",
        "reporter.view", "reporter.manage", "reporter.self", "fleet.view",
        "notifications.view", "notifications.manage",
        "chat.read", "chat.send", "chat.moderate", "chat.contacts.manage",
        "tracker.self", "tracker.viewall", "tracker.ai",
        "shares.view", "shares.manage",
        "family.use", "family.finance",
        "users.view", "users.manage", "activity.view",
    };

    [Theory]
    [InlineData("dashboard.view")]
    [InlineData("dashboard.export")]
    [InlineData("sync.run")]
    [InlineData("calendar.view")]
    [InlineData("pricing.view")]
    [InlineData("pricing.manage")]
    [InlineData("settings.view")]
    [InlineData("settings.manage")]
    [InlineData("sources.manage")]
    [InlineData("reporter.view")]
    [InlineData("reporter.manage")]
    [InlineData("reporter.self")]
    [InlineData("fleet.view")]
    [InlineData("notifications.view")]
    [InlineData("notifications.manage")]
    [InlineData("chat.read")]
    [InlineData("chat.send")]
    [InlineData("chat.moderate")]
    [InlineData("chat.contacts.manage")]
    [InlineData("tracker.self")]
    [InlineData("tracker.viewall")]
    [InlineData("tracker.ai")]
    [InlineData("shares.view")]
    [InlineData("shares.manage")]
    [InlineData("family.use")]
    [InlineData("family.finance")]
    [InlineData("users.view")]
    [InlineData("users.manage")]
    [InlineData("activity.view")]
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
        Permissions.DashboardExport.Should().Be("dashboard.export");
        Permissions.SyncRun.Should().Be("sync.run");
        Permissions.CalendarView.Should().Be("calendar.view");
        Permissions.PricingView.Should().Be("pricing.view");
        Permissions.PricingManage.Should().Be("pricing.manage");
        Permissions.SettingsView.Should().Be("settings.view");
        Permissions.SettingsManage.Should().Be("settings.manage");
        Permissions.SourcesManage.Should().Be("sources.manage");
        Permissions.ReporterView.Should().Be("reporter.view");
        Permissions.ReporterManage.Should().Be("reporter.manage");
        Permissions.ReporterSelf.Should().Be("reporter.self");
        Permissions.FleetView.Should().Be("fleet.view");
        Permissions.NotificationsView.Should().Be("notifications.view");
        Permissions.NotificationsManage.Should().Be("notifications.manage");
        Permissions.ChatRead.Should().Be("chat.read");
        Permissions.ChatSend.Should().Be("chat.send");
        Permissions.ChatModerate.Should().Be("chat.moderate");
        Permissions.ChatContactsManage.Should().Be("chat.contacts.manage");
        Permissions.TrackerSelf.Should().Be("tracker.self");
        Permissions.TrackerViewAll.Should().Be("tracker.viewall");
        Permissions.TrackerAi.Should().Be("tracker.ai");
        Permissions.SharesView.Should().Be("shares.view");
        Permissions.SharesManage.Should().Be("shares.manage");
        Permissions.FamilyUse.Should().Be("family.use");
        Permissions.FamilyFinance.Should().Be("family.finance");
        Permissions.UsersView.Should().Be("users.view");
        Permissions.UsersManage.Should().Be("users.manage");
        Permissions.ActivityView.Should().Be("activity.view");
    }

    [Fact]
    public void All_contains_exactly_the_twenty_nine_known_keys()
    {
        Permissions.All.Should().HaveCount(29);
        Permissions.All.Should().BeEquivalentTo(AllKeys);
    }

    [Fact]
    public void All_has_no_duplicates()
    {
        Permissions.All.Distinct().Count().Should().Be(Permissions.All.Length);
    }

    [Fact]
    public void Catalog_has_twenty_nine_entries()
    {
        Permissions.Catalog.Should().HaveCount(29);
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
    public void Every_catalog_entry_has_non_empty_group_label_and_description()
    {
        foreach (var info in Permissions.Catalog)
        {
            info.Group.Should().NotBeNullOrWhiteSpace();
            info.Label.Should().NotBeNullOrWhiteSpace();
            info.Description.Should().NotBeNullOrWhiteSpace();
        }
    }

    [Fact]
    public void Views_are_the_page_view_gates_and_all_valid()
    {
        // The page-view gates: every *.view key plus chat.read (the Chat page gate) and tracker.self
        // (the Tracker page gate) — both page gates without a *.view suffix. 10 *.view keys + chat.read
        // + tracker.self = 12.
        Permissions.Views.Should().HaveCount(12);
        Permissions.Views.Should().OnlyContain(k => Permissions.IsValid(k));
        Permissions.Views.Should().OnlyContain(k =>
            k.EndsWith(".view") || k == Permissions.ChatRead || k == Permissions.TrackerSelf);
        // Every *.view key in the catalog is represented in Views.
        var catalogViews = Permissions.All.Where(k => k.EndsWith(".view"));
        Permissions.Views.Should().Contain(catalogViews);
        Permissions.Views.Should().Contain(Permissions.ChatRead);
        Permissions.Views.Should().Contain(Permissions.TrackerSelf);
    }

    [Fact]
    public void ChatModerate_is_not_defaultable()
    {
        Permissions.IsDefaultable(Permissions.ChatModerate).Should().BeFalse();
        Permissions.IsDefaultable(Permissions.ChatRead).Should().BeTrue();
        Permissions.IsDefaultable(Permissions.ChatSend).Should().BeTrue();
    }

    [Fact]
    public void ChatContactsManage_is_not_defaultable()
    {
        Permissions.IsDefaultable(Permissions.ChatContactsManage).Should().BeFalse();
        Permissions.IsDefaultable(Permissions.UsersManage).Should().BeFalse();
    }

    [Fact]
    public void TrackerViewAll_is_not_defaultable_but_TrackerSelf_is()
    {
        // Reading every user's food & fitness log is a coach/admin capability that must be granted
        // deliberately; logging your own is a defaultable, per-user capability.
        Permissions.IsDefaultable(Permissions.TrackerViewAll).Should().BeFalse();
        Permissions.IsDefaultable(Permissions.TrackerSelf).Should().BeTrue();
    }

    [Fact]
    public void Family_permissions_are_not_defaultable()
    {
        // The Family Hub holds private household data + shared finances; access must be granted
        // deliberately per user, never inherited by every new account.
        Permissions.IsDefaultable(Permissions.FamilyUse).Should().BeFalse();
        Permissions.IsDefaultable(Permissions.FamilyFinance).Should().BeFalse();
    }

    [Fact]
    public void TrackerAi_is_defaultable_but_is_not_a_page_view_gate()
    {
        // AI assists IS defaultable (so it CAN be selected into the open-signup default set), even though
        // it is OFF by default and never seeded. It is not a page-view gate, so it is absent from Views.
        Permissions.IsDefaultable(Permissions.TrackerAi).Should().BeTrue();
        Permissions.Views.Should().NotContain(Permissions.TrackerAi);
    }
}
