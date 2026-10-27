using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using TurboDiscord.Database;
using TurboDiscord.Events;
using Xunit;

namespace TurboDiscord.Tests;

/// <summary>Which events become posts: routes, thresholds, dedupe, templates and the queue's bound.</summary>
public sealed class RoutingTests
{
    [Fact]
    public async Task Every_event_is_off_until_it_is_given_a_webhook_and_turned_on()
    {
        using var host = new TestHost();

        // Every known event has a route, off, so the table lists them all.
        Assert.Equal(EventCatalog.ALL.Count, host.Query(db => db.Routes.Count()));
        Assert.True(host.Query(db => db.Routes.All(x => !x.Enabled && x.WebhookId == null)));

        Assert.Equal(0, await host.RouteAsync(host.Event(EventCatalog.LTD_PURCHASE)));

        host.Route("staff", EventCatalog.LTD_PURCHASE);

        Assert.Equal(1, await host.RouteAsync(host.Event(EventCatalog.LTD_PURCHASE)));
        Assert.Equal(0, await host.RouteAsync(host.Event(EventCatalog.TRADE)));
    }

    [Fact]
    public async Task An_event_heard_twice_is_posted_once_by_its_dedupe_key()
    {
        using var host = new TestHost();

        host.Route("staff", EventCatalog.LTD_PURCHASE);

        Assert.Equal(
            1,
            await host.RouteAsync(
                host.Event(EventCatalog.LTD_PURCHASE, "ltd:op-1"),
                host.Event(EventCatalog.LTD_PURCHASE, "ltd:op-1")
            )
        );
        Assert.Equal(0, await host.RouteAsync(host.Event(EventCatalog.LTD_PURCHASE, "ltd:op-1")));
        Assert.Equal(1, await host.RouteAsync(host.Event(EventCatalog.LTD_PURCHASE, "ltd:op-2")));
        // Without a key every event is its own.
        Assert.Equal(
            2,
            await host.RouteAsync(
                host.Event(EventCatalog.LTD_PURCHASE),
                host.Event(EventCatalog.LTD_PURCHASE)
            )
        );
    }

    [Fact]
    public async Task A_threshold_counts_in_its_routes_currency_or_in_items()
    {
        using var host = new TestHost();

        host.Route("big", r => r.Threshold = 1000, EventCatalog.CATALOG_PURCHASE);
        host.Route("big", r => r.Threshold = 5, EventCatalog.TRADE);

        var purchase = (long credits, long diamonds) =>
            host.Event(
                EventCatalog.CATALOG_PURCHASE,
                worth: new() { ["credits"] = credits, ["diamonds"] = diamonds }
            );

        Assert.Equal(0, await host.RouteAsync(purchase(999, 0)));
        Assert.Equal(1, await host.RouteAsync(purchase(1000, 0)));
        // Diamonds are not credits: the route counts in credits until it is told otherwise.
        Assert.Equal(0, await host.RouteAsync(purchase(0, 5000)));

        host.Route("big", r => r.Currency = "diamonds", EventCatalog.CATALOG_PURCHASE);

        Assert.Equal(1, await host.RouteAsync(purchase(0, 5000)));

        Assert.Equal(
            0,
            await host.RouteAsync(
                host.Event(EventCatalog.TRADE, worth: new() { [DiscordEvent.ITEMS] = 4 })
            )
        );
        Assert.Equal(
            1,
            await host.RouteAsync(
                host.Event(EventCatalog.TRADE, worth: new() { [DiscordEvent.ITEMS] = 5 })
            )
        );
    }

    [Fact]
    public async Task A_post_is_written_with_the_routes_title_and_text_and_its_values_escaped()
    {
        using var host = new TestHost(new DiscordConfig { Footer = "My Hotel" });

        host.Route(
            "staff",
            r => r.Title = "LTD! {player} got {furni} #{serial} of {series} ({unknown})",
            EventCatalog.LTD_PURCHASE
        );

        var evt = host.Event(EventCatalog.LTD_PURCHASE) with
        {
            Values = new Dictionary<string, string>
            {
                ["player"] = "**@everyone**",
                ["furni"] = "throne",
                ["serial"] = "3",
                ["series"] = "50",
                ["price"] = "5 credits",
            },
            Url = "https://panel.example/players/1",
            Fields = [new("Item id", "77")],
        };

        await host.RouteAsync(evt);

        var delivery = host.Deliveries().Single();
        var embed = JsonNode.Parse(delivery.Embed)!.AsObject();

        // The Deliveries page shows the title as written.
        Assert.Equal("LTD! **@everyone** got throne #3 of 50 ({unknown})", delivery.Title);

        Assert.Equal(
            "LTD! \\*\\*@​everyone\\*\\* got throne #3 of 50 ({unknown})",
            embed["title"]!.GetValue<string>()
        );
        Assert.Equal("Paid 5 credits.", embed["description"]!.GetValue<string>());
        Assert.Equal("https://panel.example/players/1", embed["url"]!.GetValue<string>());
        Assert.Equal(0xF1C40F, embed["color"]!.GetValue<int>());
        Assert.Equal("My Hotel", embed["footer"]!["text"]!.GetValue<string>());
        Assert.Equal("Item id", embed["fields"]![0]!["name"]!.GetValue<string>());
        Assert.Equal("2026-10-21T12:00:00.0000000Z", embed["timestamp"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_full_queue_drops_its_oldest_posts_and_says_so()
    {
        using var host = new TestHost(new DiscordConfig { QueueCapacity = 3 });

        host.Route("staff", EventCatalog.PLAYER_LOGIN);

        for (var i = 0; i < 5; i++)
            await host.RouteAsync(host.Event(EventCatalog.PLAYER_LOGIN, $"login:{i}"));

        var rows = host.Deliveries();

        Assert.Equal(
            ["dropped", "dropped", "queued", "queued", "queued"],
            rows.Select(x => x.Status)
        );
        Assert.Equal(
            ["login:0", "login:1"],
            rows.Where(x => x.Status == DeliveryEntity.DROPPED).Select(x => x.DedupeKey)
        );
        Assert.Contains("queue was full", rows[0].Error);
    }

    [Fact]
    public async Task Nothing_is_queued_while_the_plugin_is_off()
    {
        using var host = new TestHost(new DiscordConfig { Enabled = false });

        host.Route("staff", EventCatalog.LTD_PURCHASE);

        Assert.Equal(0, await host.RouteAsync(host.Event(EventCatalog.LTD_PURCHASE)));
    }
}
