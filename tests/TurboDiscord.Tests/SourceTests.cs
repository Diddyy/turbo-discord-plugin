using System;
using System.Collections.Immutable;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Turbo.Primitives.Availability;
using Turbo.Primitives.Moderation;
using Turbo.Primitives.Moderation.Events;
using Turbo.Primitives.Moderation.Snapshots;
using Turbo.Primitives.Players;
using Turbo.Primitives.Players.Events;
using Turbo.Primitives.Plugins.Events;
using TurboDiscord.Events;
using TurboDiscord.Sources;
using Xunit;

namespace TurboDiscord.Tests;

/// <summary>The hotel's events and other plugins' notices, from the handler to the post.</summary>
public sealed class SourceTests
{
    private static (
        HotelEvents Events,
        Fake<IModerationIssueService> Issues,
        Fake<ISanctionService> Sanctions
    ) Hotel(TestHost host)
    {
        var (issues, issueFake) = Fake<IModerationIssueService>.Create();
        var (sanctions, sanctionFake) = Fake<ISanctionService>.Create();

        return (
            new HotelEvents(
                host.Inbox,
                host.Scopes,
                host.Router,
                host.Switches,
                host.Names,
                issues,
                sanctions,
                host.Time,
                NullLogger<HotelEvents>.Instance
            ),
            issueFake,
            sanctionFake
        );
    }

    private static ModIssueSnapshot Issue(int id, int state, int ageMs) =>
        new()
        {
            IssueId = id,
            State = state,
            CategoryId = 4,
            ReportedCategoryId = 0,
            IssueAgeInMilliseconds = ageMs,
            Priority = 0,
            GroupingId = 0,
            ReporterUserId = 1,
            ReporterUserName = "Alice",
            ReportedUserId = 2,
            ReportedUserName = "Bob",
            PickerUserId = 0,
            PickerUserName = "",
            Message = "He scammed me",
            ChatRecordId = 0,
        };

    [Fact]
    public async Task A_new_report_is_posted_once_and_a_picked_or_old_one_is_not()
    {
        using var host = new TestHost();
        var (hotel, issues, _) = Hotel(host);

        host.Route("mods", EventCatalog.REPORT_NEW);
        issues.Answers[nameof(IModerationIssueService.GetOpenIssuesAsync)] = _ =>
            Task.FromResult(
                ImmutableArray.Create(
                    Issue(10, ModIssueSnapshot.STATE_OPEN, 1500),
                    Issue(11, ModIssueSnapshot.STATE_PICKED, 1500),
                    Issue(12, ModIssueSnapshot.STATE_OPEN, 600_000)
                )
            );

        var handler = new CallForHelpHandler(host.Switches, host.Inbox);

        await handler.HandleAsync(
            new CallForHelpChangedEvent { IssueIds = [10, 11, 12] },
            new(),
            TestHost.CT
        );
        // Raised again when it is picked: already posted.
        await handler.HandleAsync(
            new CallForHelpChangedEvent { IssueIds = [10] },
            new(),
            TestHost.CT
        );
        await hotel.DrainAsync(TestHost.CT);
        await host.Sender.SendDueAsync(flush: true, TestHost.CT);

        var embed = Assert.Single(host.Discord.Embeds);
        Assert.Equal("Alice reported Bob", embed["title"]!.GetValue<string>());
        Assert.Equal("He scammed me", embed["description"]!.GetValue<string>());
        Assert.Equal("https://panel.example/reports/10", embed["url"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_ban_and_its_lifting_are_posted_with_who_and_until_when()
    {
        using var host = new TestHost();
        var (hotel, _, sanctions) = Hotel(host);
        var handler = new SanctionHandler(host.Switches, host.Inbox);

        // Off: not even looked up.
        await handler.HandleAsync(
            new PlayerSanctionChangedEvent { PlayerId = PlayerId.Parse(2) },
            new(),
            TestHost.CT
        );
        Assert.Equal(0, await hotel.DrainAsync(TestHost.CT));

        host.Route("mods", EventCatalog.PLAYER_BANNED, EventCatalog.PLAYER_UNBANNED);
        sanctions.Answers[nameof(ISanctionService.GetActiveBanAsync)] = _ =>
            Task.FromResult<PlayerSanctionSnapshot?>(
                new PlayerSanctionSnapshot
                {
                    Id = 5,
                    PlayerId = PlayerId.Parse(2),
                    Kind = default,
                    Reason = "Scamming",
                    IssuerId = PlayerId.Parse(3),
                    IssuedAtUtc = host.Now,
                    ExpiresAtUtc = new DateTime(2026, 10, 28, 12, 0, 0, DateTimeKind.Utc),
                }
            );

        await handler.HandleAsync(
            new PlayerSanctionChangedEvent { PlayerId = PlayerId.Parse(2) },
            new(),
            TestHost.CT
        );
        Assert.Equal(1, await hotel.DrainAsync(TestHost.CT));

        sanctions.Answers[nameof(ISanctionService.GetActiveBanAsync)] = _ =>
            Task.FromResult<PlayerSanctionSnapshot?>(null);
        await handler.HandleAsync(
            new PlayerSanctionChangedEvent { PlayerId = PlayerId.Parse(2) },
            new(),
            TestHost.CT
        );
        Assert.Equal(1, await hotel.DrainAsync(TestHost.CT));

        await host.Sender.SendDueAsync(flush: true, TestHost.CT);

        var embeds = host.Discord.Embeds.ToList();
        Assert.Equal(
            ["Bob was banned", "Bob's ban was lifted"],
            embeds.Select(x => x["title"]!.GetValue<string>())
        );
        Assert.Equal("Scamming", embeds[0]["description"]!.GetValue<string>());
        Assert.Equal(
            ["Staffy", "2026-10-28 12:00 UTC"],
            embeds[0]["fields"]!.AsArray().Select(x => x!["value"]!.GetValue<string>())
        );
    }

    [Fact]
    public async Task Logins_are_dropped_where_they_are_heard_while_their_route_is_off()
    {
        using var host = new TestHost();
        var (hotel, _, _) = Hotel(host);
        var handler = new OnlineHandler(host.Switches, host.Inbox);

        await handler.HandleAsync(
            new PlayerOnlineChangedEvent { PlayerId = PlayerId.Parse(1), Online = true },
            new(),
            TestHost.CT
        );
        Assert.False(host.Inbox.Reader.TryPeek(out _));

        host.Route("log", EventCatalog.PLAYER_LOGIN);
        await handler.HandleAsync(
            new PlayerOnlineChangedEvent { PlayerId = PlayerId.Parse(1), Online = true },
            new(),
            TestHost.CT
        );
        await handler.HandleAsync(
            new PlayerOnlineChangedEvent { PlayerId = PlayerId.Parse(1), Online = false },
            new(),
            TestHost.CT
        );
        Assert.Equal(1, await hotel.DrainAsync(TestHost.CT));
    }

    [Fact]
    public async Task A_plugins_notice_gets_a_route_off_the_first_time_and_is_posted_once_per_key_once_on()
    {
        using var host = new TestHost();
        var (hotel, _, _) = Hotel(host);
        var handler = new PluginNoticeHandler(host.Switches, host.Inbox);
        var notice = new PluginNoticeEvent
        {
            Source = "clienterrors",
            Topic = "issue.new",
            Title = "New client error #4",
            Text = "Cannot read properties of undefined",
            Url = "https://panel.example/plugin/clienterrors/issue/4",
            Fields = [new("Build", "a1b2c3")],
            Level = PluginNoticeLevel.Error,
            Key = "4",
        };

        await handler.HandleAsync(notice, new(), TestHost.CT);
        Assert.Equal(0, await hotel.DrainAsync(TestHost.CT));

        var route = host.Query(db =>
            db.Routes.Single(x => x.Key == "plugin:clienterrors:issue.new")
        );
        Assert.False(route.Enabled);

        // Known and off: dropped where it is heard.
        await handler.HandleAsync(notice, new(), TestHost.CT);
        Assert.False(host.Inbox.Reader.TryPeek(out _));

        host.Route("dev", "plugin:clienterrors:issue.new");
        await handler.HandleAsync(notice, new(), TestHost.CT);
        await handler.HandleAsync(notice, new(), TestHost.CT);
        await handler.HandleAsync(
            notice with
            {
                Key = "5",
                Title = "New client error #5",
            },
            new(),
            TestHost.CT
        );
        Assert.Equal(2, await hotel.DrainAsync(TestHost.CT));

        await host.Sender.SendDueAsync(flush: true, TestHost.CT);

        var embeds = host.Discord.Embeds.ToList();
        Assert.Equal(
            ["New client error \\#4", "New client error \\#5"],
            embeds.Select(x => x["title"]!.GetValue<string>())
        );
        Assert.Equal(0xE74C3C, embeds[0]["color"]!.GetValue<int>());
        Assert.Equal("a1b2c3", embeds[0]["fields"]![0]!["value"]!.GetValue<string>());
        Assert.Equal(
            "https://panel.example/plugin/clienterrors/issue/4",
            embeds[0]["url"]!.GetValue<string>()
        );
    }

    [Fact]
    public async Task Countdowns_are_posted_when_they_start_or_are_called_off_but_not_what_was_there_at_start()
    {
        using var host = new TestHost();
        var (hotel, _, _) = Hotel(host);
        var (availability, fake) = Fake<IHotelAvailability>.Create();
        var current = new HotelAvailabilitySnapshot(
            HotelAvailabilityPhase.MaintenanceScheduled,
            host.Now.AddMinutes(20),
            "Old"
        );

        fake.Answers["get_Current"] = _ => current;
        host.Route("ops", EventCatalog.HOTEL_COUNTDOWN, EventCatalog.HOTEL_COUNTDOWN_CANCELLED);

        using var watch = new ServerWatch(host.Inbox, availability, host.Time);
        var lifetime = new Microsoft.Extensions.Hosting.Internal.ApplicationLifetime(
            NullLogger<Microsoft.Extensions.Hosting.Internal.ApplicationLifetime>.Instance
        );

        watch.Start(lifetime, () => { });
        watch.Poll();
        Assert.Equal(0, await hotel.DrainAsync(TestHost.CT));

        current = new HotelAvailabilitySnapshot(
            HotelAvailabilityPhase.ShutdownScheduled,
            host.Now.AddMinutes(5),
            "Update",
            Restart: true
        );
        watch.Poll();
        watch.Poll();
        current = HotelAvailabilitySnapshot.Open;
        watch.Poll();
        Assert.Equal(2, await hotel.DrainAsync(TestHost.CT));

        await host.Sender.SendDueAsync(flush: true, TestHost.CT);
        Assert.Equal(
            ["Restart in 5 minutes", "Restart called off"],
            host.Discord.Embeds.Select(x => x["title"]!.GetValue<string>())
        );
        Assert.Equal("Update", host.Discord.Embeds.First()["description"]!.GetValue<string>());
    }
}
