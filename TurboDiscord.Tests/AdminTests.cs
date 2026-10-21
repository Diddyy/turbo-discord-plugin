using System.Linq;
using System.Threading.Tasks;
using TurboDiscord.Database;
using TurboDiscord.Events;
using Xunit;

namespace TurboDiscord.Tests;

/// <summary>The admin pages' endpoints: webhooks, routing and deliveries.</summary>
public sealed class AdminTests
{
    [Fact]
    public async Task A_webhook_is_added_only_to_discord_and_its_token_is_never_shown_back()
    {
        using var host = new TestHost();
        var admin = host.Admin();

        Assert.Equal(
            ["plugin.discord.view", "plugin.discord.manage", "plugin.discord.webhooks"],
            admin.Nodes
        );

        var refused = await admin.CallAsync(
            "POST webhooks",
            body: """{"name":"staff","url":"https://evil.example/api/webhooks/1/x"}"""
        );
        Assert.Equal(400, refused.Status);
        Assert.Contains("AllowedHosts", refused.Error);

        Assert.Equal(
            400,
            (
                await admin.CallAsync(
                    "POST webhooks",
                    body: """{"name":"no spaces","url":"https://discord.com/api/webhooks/1/x"}"""
                )
            ).Status
        );

        var added = await admin.CallAsync(
            "POST webhooks",
            body: $$"""{"name":"staff","url":"{{TestHost.HOOK_URL}}"}"""
        );
        Assert.Equal(200, added.Status);
        Assert.Equal(
            400,
            (
                await admin.CallAsync(
                    "POST webhooks",
                    body: $$"""{"name":"staff","url":"{{TestHost.HOOK_URL}}"}"""
                )
            ).Status
        );

        var list = Admin.Json(await admin.CallAsync("GET webhooks"));
        var one = Admin.Json(await admin.CallAsync("GET webhooks/{id}", 1));

        Assert.Contains("discord.com webhook 123456", list);
        Assert.DoesNotContain("secret-token", list + one + Admin.Json(added));
    }

    [Fact]
    public async Task A_route_is_given_a_webhook_title_and_threshold_then_turned_on_and_its_test_post_goes()
    {
        using var host = new TestHost();
        var admin = host.Admin();

        await admin.CallAsync(
            "POST webhooks",
            body: $$"""{"name":"staff","url":"{{TestHost.HOOK_URL}}"}"""
        );

        var id = host.Query(db => db.Routes.Single(x => x.Key == EventCatalog.CATALOG_PURCHASE).Id);

        Assert.Equal(400, (await admin.CallAsync("POST routes/{id}/on", id)).Status);
        Assert.Equal(
            400,
            (await admin.CallAsync("POST routes/{id}", id, body: """{"webhook":"nope"}""")).Status
        );
        Assert.Equal(
            400,
            (await admin.CallAsync("POST routes/{id}", id, body: """{"currency":"gold"}""")).Status
        );
        Assert.Equal(
            200,
            (
                await admin.CallAsync(
                    "POST routes/{id}",
                    id,
                    body: """{"webhook":"staff","title":"Big spender: {player}","threshold":5000,"currency":"diamonds"}"""
                )
            ).Status
        );
        Assert.Equal(200, (await admin.CallAsync("POST routes/{id}/on", id)).Status);

        var route = host.Query(db => db.Routes.Single(x => x.Id == id));
        Assert.Equal(
            (true, "Big spender: {player}", 5000L, "diamonds"),
            (route.Enabled, route.Title, route.Threshold, route.Currency)
        );
        Assert.True(host.Switches.IsOn(EventCatalog.CATALOG_PURCHASE));
        Assert.True(host.Switches.AnyFromMovements);

        var detail = Admin.Json(await admin.CallAsync("GET routes/{id}", id));
        Assert.Contains("spent at least 5,000 diamonds", detail);
        Assert.Contains("{player}", detail);

        Assert.Contains(
            "Catalogue purchase",
            Admin.Json(await admin.CallAsync("GET routes", query: "state=on"))
        );

        // A trade counts items, not a currency.
        var trade = host.Query(db => db.Routes.Single(x => x.Key == EventCatalog.TRADE).Id);
        Assert.Equal(
            400,
            (
                await admin.CallAsync("POST routes/{id}", trade, body: """{"currency":"credits"}""")
            ).Status
        );
        // A login has no threshold.
        var login = host.Query(db => db.Routes.Single(x => x.Key == EventCatalog.PLAYER_LOGIN).Id);
        Assert.Equal(
            400,
            (await admin.CallAsync("POST routes/{id}", login, body: """{"threshold":3}""")).Status
        );

        Assert.Equal(200, (await admin.CallAsync("POST webhooks/{id}/test", 1)).Status);
        Assert.Equal(
            "Test post to staff",
            Assert.Single(host.Discord.Embeds)["title"]!.GetValue<string>()
        );

        Assert.Equal(200, (await admin.CallAsync("POST routes/{id}/off", id)).Status);
        Assert.False(host.Switches.IsOn(EventCatalog.CATALOG_PURCHASE));
    }

    [Fact]
    public async Task Removing_a_webhook_unroutes_its_events_and_fails_what_waits_and_a_failed_post_can_go_again()
    {
        using var host = new TestHost();
        var admin = host.Admin();

        host.Route("staff", EventCatalog.PLAYER_BANNED);
        await host.RouteAsync(host.Event(EventCatalog.PLAYER_BANNED, "ban:1"));

        host.Discord.Answers.Enqueue(() =>
            FakeDiscord.Answer(System.Net.HttpStatusCode.BadRequest, """{"message":"bad"}""")
        );
        await host.Sender.SendDueAsync(flush: true, TestHost.CT);
        Assert.Equal(DeliveryEntity.FAILED, host.Deliveries().Single().Status);

        Assert.Contains(
            "failed",
            Admin.Json(await admin.CallAsync("GET deliveries", query: "status=failed"))
        );
        Assert.Contains(
            "\"retryable\":true",
            Admin.Json(await admin.CallAsync("GET deliveries/{id}", 1))
        );
        Assert.Equal(200, (await admin.CallAsync("POST deliveries/{id}/retry", 1)).Status);
        await host.Sender.SendDueAsync(flush: true, TestHost.CT);
        Assert.Equal(DeliveryEntity.SENT, host.Deliveries().Single().Status);

        await host.RouteAsync(host.Event(EventCatalog.PLAYER_BANNED, "ban:2"));
        Assert.Equal(204, (await admin.CallAsync("DELETE webhooks/{id}", 1)).Status);

        Assert.Equal("The webhook was removed.", host.Deliveries()[1].Error);
        Assert.Null(
            host.Query(db => db.Routes.Single(x => x.Key == EventCatalog.PLAYER_BANNED).WebhookId)
        );
        Assert.False(host.Switches.IsOn(EventCatalog.PLAYER_BANNED));
    }

    [Fact]
    public void The_pages_are_the_routing_table_webhooks_and_deliveries()
    {
        using var host = new TestHost();
        var admin = host.Admin();

        Assert.Equal(
            ["routing", "route", "webhooks", "webhook", "deliveries", "delivery"],
            admin.Pages.Select(x => x.Id)
        );
        Assert.Equal(
            ["routing", "webhooks", "deliveries"],
            admin.Pages.Where(x => x.Nav).Select(x => x.Id)
        );
        Assert.All(
            admin.Pages.SelectMany(x => x.Blocks).Where(x => x.Endpoint is not null),
            b => Assert.Contains(admin.Map.Keys, k => k == $"GET {b.Endpoint}")
        );
    }
}
