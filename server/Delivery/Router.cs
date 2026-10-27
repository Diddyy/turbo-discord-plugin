using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TurboDiscord.Database;
using TurboDiscord.Events;

namespace TurboDiscord.Delivery;

/// <summary>
/// Turns events into posts waiting in <c>discord_deliveries</c>: each to its route's webhook when
/// the route is on and the event reaches its threshold, written with the route's title and text,
/// and only once per dedupe key. It writes in the caller's context, so posts made from asset
/// movements commit with the movement cursor or not at all. Past
/// <see cref="DiscordConfig.QueueCapacity"/> the oldest waiting posts are dropped.
/// </summary>
public sealed class Router(IOptionsMonitor<DiscordConfig> options, TimeProvider time)
{
    /// <summary>Raised after posts were queued, so the sender looks at once.</summary>
    public event Action? Queued;

    /// <summary>
    /// Queues the posts of <paramref name="events"/> and saves them; how many were queued. A plugin
    /// notice topic heard for the first time gets a route, off.
    /// </summary>
    public async Task<int> RouteAsync(
        DiscordDbContext db,
        IReadOnlyList<DiscordEvent> events,
        CancellationToken ct
    )
    {
        var config = options.CurrentValue;

        if (!config.Enabled || events.Count == 0)
            return 0;

        var now = time.GetUtcNow().UtcDateTime;
        var keys = events.Select(x => x.Key).Distinct().ToList();
        var routes = await db
            .Routes.Where(x => keys.Contains(x.Key))
            .ToDictionaryAsync(x => x.Key, ct)
            .ConfigureAwait(false);

        foreach (
            var key in keys.Where(x => !routes.ContainsKey(x) && EventCatalog.Find(x) is not null)
        )
        {
            var route = new RouteEntity { Key = key, UpdatedAt = now };

            db.Routes.Add(route);
            routes[key] = route;
        }

        var webhooks = await db.Webhooks.Select(x => x.Id).ToListAsync(ct).ConfigureAwait(false);
        var dedupe = events.Select(x => x.DedupeKey).OfType<string>().Distinct().ToList();
        var seen = (
            await db
                .Deliveries.Where(x => x.DedupeKey != null && dedupe.Contains(x.DedupeKey))
                .Select(x => x.DedupeKey!)
                .ToListAsync(ct)
                .ConfigureAwait(false)
        ).ToHashSet();
        var queued = 0;

        foreach (var evt in events)
        {
            if (
                !routes.TryGetValue(evt.Key, out var route)
                || !route.Enabled
                || route.WebhookId is not { } webhook
                || !webhooks.Contains(webhook)
                || !Reaches(evt, route)
                || EventCatalog.Find(evt.Key) is not { } info
            )
                continue;

            if (evt.DedupeKey is { } key && !seen.Add(key))
                continue;

            var embed = Embeds.Build(evt, info, route.Title, route.Text, config.Footer);

            db.Deliveries.Add(
                new DeliveryEntity
                {
                    EventKey = evt.Key,
                    WebhookId = webhook,
                    DedupeKey = evt.DedupeKey is { } k
                        ? Embeds.Cut(k, DeliveryEntity.MAX_DEDUPE)
                        : null,
                    Embed = embed.ToJsonString(),
                    // As the panel shows it: unescaped.
                    Title = Embeds.Cut(
                        Embeds.Render(route.Title ?? info.Title, evt.Values, escape: false),
                        RouteEntity.MAX_TITLE
                    ),
                    CreatedAt = now,
                }
            );
            queued++;
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        if (queued > 0)
        {
            await DropOverCapacityAsync(db, config.QueueCapacity, now, ct).ConfigureAwait(false);
            Queued?.Invoke();
        }

        return queued;
    }

    /// <summary>Whether <paramref name="evt"/> is worth at least the route's threshold, in its currency.</summary>
    public static bool Reaches(DiscordEvent evt, RouteEntity route)
    {
        if (route.Threshold <= 0)
            return true;

        var info = EventCatalog.Find(route.Key);

        if (info?.Threshold is null)
            return true;

        var unit = info.Currency is null ? DiscordEvent.ITEMS : route.Currency ?? info.Currency;

        return evt.Worth.TryGetValue(unit, out var worth) && worth >= route.Threshold;
    }

    /// <summary>
    /// Drops the oldest waiting posts past <paramref name="capacity"/>: a queue Discord could not
    /// take for long keeps what is newest, and what it dropped is counted on the page.
    /// </summary>
    private static async Task DropOverCapacityAsync(
        DiscordDbContext db,
        int capacity,
        DateTime now,
        CancellationToken ct
    )
    {
        var waiting = await db
            .Deliveries.CountAsync(x => x.Status == DeliveryEntity.QUEUED, ct)
            .ConfigureAwait(false);

        if (capacity <= 0 || waiting <= capacity)
            return;

        var oldest = await db
            .Deliveries.Where(x => x.Status == DeliveryEntity.QUEUED)
            .OrderBy(x => x.Id)
            .Take(waiting - capacity)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        foreach (var row in oldest)
        {
            row.Status = DeliveryEntity.DROPPED;
            row.DoneAt = now;
            row.Error = $"The queue was full ({capacity} waiting): dropped for newer posts.";
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}
