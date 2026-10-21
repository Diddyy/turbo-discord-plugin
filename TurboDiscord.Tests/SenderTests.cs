using System;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using TurboDiscord.Database;
using TurboDiscord.Delivery;
using TurboDiscord.Events;
using Xunit;

namespace TurboDiscord.Tests;

/// <summary>How posts reach Discord: batched, paced per webhook, and what each answer does.</summary>
public sealed class SenderTests
{
    private static async Task QueueAsync(
        TestHost host,
        int count,
        string key = EventCatalog.PLAYER_LOGIN
    )
    {
        for (var i = 0; i < count; i++)
            await host.RouteAsync(host.Event(key) with { DedupeKey = $"{key}:{Guid.NewGuid()}" });
    }

    [Fact]
    public async Task A_burst_waits_for_its_batch_window_and_goes_as_one_message_of_up_to_ten_embeds()
    {
        using var host = new TestHost();

        host.Route("staff", EventCatalog.PLAYER_LOGIN);
        await QueueAsync(host, 3);

        // Within the window: the first post waits for others.
        Assert.Equal(0, await host.Sender.SendDueAsync(flush: false, TestHost.CT));
        Assert.Empty(host.Discord.Posts);

        host.Time.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(1, await host.Sender.SendDueAsync(flush: false, TestHost.CT));

        var post = Assert.Single(host.Discord.Posts);
        Assert.Equal(3, post.Body["embeds"]!.AsArray().Count);
        Assert.Empty(post.Body["allowed_mentions"]!["parse"]!.AsArray());
        Assert.All(host.Deliveries(), x => Assert.Equal(DeliveryEntity.SENT, x.Status));

        // Twelve at once: ten go at once (no need to wait), then the other two.
        await QueueAsync(host, 12);
        Assert.Equal(1, await host.Sender.SendDueAsync(flush: false, TestHost.CT));
        Assert.Equal(10, host.Discord.Posts[^1].Body["embeds"]!.AsArray().Count);
        host.Time.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(1, await host.Sender.SendDueAsync(flush: false, TestHost.CT));
        Assert.Equal(2, host.Discord.Posts[^1].Body["embeds"]!.AsArray().Count);
    }

    [Fact]
    public async Task A_429_holds_the_webhook_for_its_retry_after_without_counting_a_failure()
    {
        using var host = new TestHost();

        host.Route("staff", EventCatalog.PLAYER_LOGIN);
        await QueueAsync(host, 1);
        host.Discord.Answers.Enqueue(() =>
            FakeDiscord.Answer(
                HttpStatusCode.TooManyRequests,
                """{"message":"You are being rate limited.","retry_after":3.5,"global":false}"""
            )
        );

        Assert.Equal(0, await host.Sender.SendDueAsync(flush: true, TestHost.CT));

        var row = Assert.Single(host.Deliveries());
        Assert.Equal(DeliveryEntity.QUEUED, row.Status);
        Assert.Equal(0, row.Attempts);

        // Held: nothing is posted until the 3.5 s are up.
        host.Time.Advance(TimeSpan.FromSeconds(3));
        Assert.Equal(0, await host.Sender.SendDueAsync(flush: true, TestHost.CT));
        Assert.Single(host.Discord.Posts);

        host.Time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(1, await host.Sender.SendDueAsync(flush: true, TestHost.CT));
        Assert.Equal(DeliveryEntity.SENT, Assert.Single(host.Deliveries()).Status);
    }

    [Fact]
    public async Task Discord_down_is_tried_again_ever_further_apart_until_max_attempts()
    {
        using var host = new TestHost(new DiscordConfig { MaxAttempts = 3 });

        host.Route("staff", EventCatalog.PLAYER_LOGIN);
        await QueueAsync(host, 1);

        for (var i = 0; i < 3; i++)
            host.Discord.Answers.Enqueue(() => FakeDiscord.Answer(HttpStatusCode.BadGateway));

        await host.Sender.SendDueAsync(flush: true, TestHost.CT);

        var row = Assert.Single(host.Deliveries());
        Assert.Equal((DeliveryEntity.QUEUED, 1), (row.Status, row.Attempts));
        Assert.Equal(host.Now.AddSeconds(2), row.NextAttemptAt);

        // Not due yet.
        await host.Sender.SendDueAsync(flush: true, TestHost.CT);
        Assert.Single(host.Discord.Posts);

        host.Time.Advance(TimeSpan.FromSeconds(2));
        await host.Sender.SendDueAsync(flush: true, TestHost.CT);
        row = Assert.Single(host.Deliveries());
        Assert.Equal(host.Now.AddSeconds(4), row.NextAttemptAt);

        host.Time.Advance(TimeSpan.FromSeconds(4));
        await host.Sender.SendDueAsync(flush: true, TestHost.CT);
        row = Assert.Single(host.Deliveries());
        Assert.Equal((DeliveryEntity.FAILED, 3), (row.Status, row.Attempts));
        Assert.Equal("Discord answered 502", row.Error);
        Assert.Equal("Discord answered 502", host.Query(db => db.Webhooks.Single().LastError));
        Assert.Equal(TimeSpan.FromMinutes(10), DiscordSender.Backoff(30));
    }

    [Fact]
    public async Task A_refused_message_is_split_so_only_the_bad_embed_fails()
    {
        using var host = new TestHost();

        host.Route("staff", EventCatalog.PLAYER_LOGIN);
        await QueueAsync(host, 3);
        host.Discord.Answers.Enqueue(() =>
            FakeDiscord.Answer(
                HttpStatusCode.BadRequest,
                """{"message":"Invalid Form Body","code":50035}"""
            )
        );
        host.Discord.Answers.Enqueue(() => FakeDiscord.Answer(HttpStatusCode.NoContent));
        host.Discord.Answers.Enqueue(() =>
            FakeDiscord.Answer(
                HttpStatusCode.BadRequest,
                """{"message":"Invalid Form Body","code":50035}"""
            )
        );

        await host.Sender.SendDueAsync(flush: true, TestHost.CT);

        Assert.Equal(
            [3, 1, 1, 1],
            host.Discord.Posts.Select(x => x.Body["embeds"]!.AsArray().Count)
        );
        Assert.Equal(
            [DeliveryEntity.SENT, DeliveryEntity.FAILED, DeliveryEntity.SENT],
            host.Deliveries().Select(x => x.Status)
        );
        Assert.Equal("Discord answered 400: Invalid Form Body", host.Deliveries()[1].Error);
    }

    [Fact]
    public async Task A_webhook_deleted_in_discord_fails_its_posts_and_says_why()
    {
        using var host = new TestHost();

        host.Route("staff", EventCatalog.PLAYER_LOGIN);
        await QueueAsync(host, 2);
        host.Discord.Answers.Enqueue(() =>
            FakeDiscord.Answer(
                HttpStatusCode.NotFound,
                """{"message":"Unknown Webhook","code":10015}"""
            )
        );

        await host.Sender.SendDueAsync(flush: true, TestHost.CT);

        Assert.All(
            host.Deliveries(),
            x =>
                Assert.Equal(
                    (DeliveryEntity.FAILED, "Discord answered 404: Unknown Webhook"),
                    (x.Status, x.Error)
                )
        );
        Assert.Equal(
            "Discord answered 404: Unknown Webhook",
            host.Query(db => db.Webhooks.Single().LastError)
        );
    }

    [Fact]
    public async Task Each_webhook_is_held_to_max_per_minute_and_to_an_empty_bucket_discord_reports()
    {
        using var host = new TestHost(new DiscordConfig { MaxPerMinute = 2, BatchWindowMs = 0 });

        host.Route("staff", EventCatalog.PLAYER_LOGIN);
        host.Route("other", EventCatalog.PLAYER_LOGOUT);

        for (var i = 0; i < 3; i++)
        {
            await QueueAsync(host, 1);
            await host.Sender.SendDueAsync(flush: false, TestHost.CT);
        }

        Assert.Equal(2, host.Discord.Posts.Count);

        // Another webhook has a minute of its own.
        await QueueAsync(host, 1, EventCatalog.PLAYER_LOGOUT);
        await host.Sender.SendDueAsync(flush: false, TestHost.CT);
        Assert.Equal(3, host.Discord.Posts.Count);

        host.Time.Advance(TimeSpan.FromSeconds(61));
        await host.Sender.SendDueAsync(flush: false, TestHost.CT);
        Assert.Equal(4, host.Discord.Posts.Count);

        // Discord's own bucket: remaining 0, reset after 2 s.
        var limiter = new WebhookLimiter();
        limiter.Block(1, host.Now, TimeSpan.FromSeconds(2));
        Assert.False(limiter.TryTake(1, host.Now.AddSeconds(1), 30));
        Assert.True(limiter.TryTake(1, host.Now.AddSeconds(2), 30));
    }
}
