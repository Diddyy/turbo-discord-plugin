using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Turbo.Contracts.Plugins.Admin;
using TurboDiscord.Database;
using TurboDiscord.Delivery;
using TurboDiscord.Events;
using TurboDiscord.Sources;

namespace TurboDiscord;

/// <summary>
/// The Discord pages of the admin panel: the webhooks (add, test, remove; the URL is never shown
/// back, only its host and webhook id), the routing table (each event on or off, its webhook, title,
/// text and threshold), and the deliveries (waiting, sent, failed, dropped, with Discord's answer;
/// a failed one can be sent again). Seeing them needs <c>plugin.discord.view</c>; routing and
/// retrying <c>plugin.discord.manage</c>; adding, testing and removing webhooks
/// <c>plugin.discord.webhooks</c>. No one holds them until a hotel grants them.
/// </summary>
public sealed class DiscordAdmin(
    IServiceScopeFactory scopes,
    IOptionsMonitor<DiscordConfig> options,
    DiscordClient client,
    WebhookLimiter limiter,
    RouteSwitches switches,
    HotelInbox inbox,
    TimeProvider time,
    IConfiguration? configuration = null
) : IPluginAdmin
{
    public const int PAGE_SIZE = 25;

    public void Configure(IPluginAdminBuilder admin)
    {
        var view = admin.Node("view", "See the Discord webhooks, routing and deliveries");
        var manage = admin.Node(
            "manage",
            "Route events to Discord webhooks and send failed posts again"
        );
        var webhooks = admin.Node("webhooks", "Add, test and remove Discord webhooks");

        admin.Map("GET", "stats", view, (_, ct) => StatsAsync(ct));
        admin.Map("GET", "webhooks", view, (_, ct) => WebhooksAsync(ct));
        admin.Map("POST", "webhooks", webhooks, AddWebhookAsync);
        admin.Map("GET", "webhooks/{id}", view, WebhookAsync);
        admin.Map("POST", "webhooks/{id}/test", webhooks, TestWebhookAsync);
        admin.Map("DELETE", "webhooks/{id}", webhooks, RemoveWebhookAsync);
        admin.Map("GET", "routes", view, RoutesAsync);
        admin.Map("GET", "routes/{id}", view, RouteAsync);
        admin.Map("POST", "routes/{id}", manage, EditRouteAsync);
        admin.Map("POST", "routes/{id}/on", manage, (r, ct) => SwitchAsync(r, true, ct));
        admin.Map("POST", "routes/{id}/off", manage, (r, ct) => SwitchAsync(r, false, ct));
        admin.Map("POST", "routes/{id}/reset", manage, ResetRouteAsync);
        admin.Map("GET", "deliveries", view, DeliveriesAsync);
        admin.Map("GET", "deliveries/{id}", view, DeliveryAsync);
        admin.Map("POST", "deliveries/{id}/retry", manage, RetryAsync);

        admin.Page(
            new PluginAdminPage(
                "routing",
                "Discord",
                view,
                [
                    new PluginAdminBlock("stats", Endpoint: "stats"),
                    new PluginAdminBlock(
                        "table",
                        Title: "Events",
                        Endpoint: "routes",
                        Columns:
                        [
                            new PluginAdminColumn("event", "Event"),
                            new PluginAdminColumn("enabled", "On", "bool"),
                            new PluginAdminColumn("webhook", "Webhook"),
                            new PluginAdminColumn("threshold", "Threshold"),
                            new PluginAdminColumn("title", "Title"),
                            new PluginAdminColumn("key", "Key"),
                        ],
                        Filters:
                        [
                            new PluginAdminField(
                                "state",
                                "State",
                                "select",
                                Options: ["", "on", "off"]
                            ),
                            new PluginAdminField("q", "Event"),
                        ],
                        ItemPage: "route"
                    ),
                    new PluginAdminBlock(
                        "text",
                        Title: "How it works",
                        Text: "Each event is off until you turn it on and give it a webhook (Webhooks page). A post waits a moment for others to the same webhook, so a burst is one message of up to 10 embeds, and each webhook is held to MaxPerMinute and to what Discord says (429).\n\n"
                            + "LTDs, purchases, trades, marketplace sales and staff gifts are read from the asset movements: they need **Turbo:Movements:Enabled** on. Each is posted once, however often it is read.\n\n"
                            + "Other plugins' notices show here as **plugin:<plugin>:<topic>** the first time one is raised."
                    ),
                ],
                "Post the hotel's events to Discord channels."
            )
        );
        admin.Page(
            new PluginAdminPage(
                "route",
                "Discord event",
                view,
                [
                    new PluginAdminBlock(
                        "detail",
                        Endpoint: "routes/{id}",
                        Columns:
                        [
                            new PluginAdminColumn("event", "Event"),
                            new PluginAdminColumn("description", "What it is"),
                            new PluginAdminColumn("enabled", "On", "bool"),
                            new PluginAdminColumn("webhook", "Webhook"),
                            new PluginAdminColumn("title", "Title"),
                            new PluginAdminColumn("text", "Text"),
                            new PluginAdminColumn("threshold", "Threshold"),
                            new PluginAdminColumn("placeholders", "Placeholders"),
                            new PluginAdminColumn("key", "Key"),
                            new PluginAdminColumn("updatedAt", "Changed", "time"),
                        ]
                    ),
                    new PluginAdminBlock(
                        "actions",
                        Actions:
                        [
                            new PluginAdminAction(
                                "Turn on",
                                "POST",
                                "routes/{id}/on",
                                Node: manage,
                                When: "enabled=false"
                            ),
                            new PluginAdminAction(
                                "Turn off",
                                "POST",
                                "routes/{id}/off",
                                Node: manage,
                                When: "enabled=true"
                            ),
                            new PluginAdminAction(
                                "Reset title and text",
                                "POST",
                                "routes/{id}/reset",
                                "Write this event with its own title and text again?",
                                Node: manage,
                                When: "custom=true"
                            ),
                        ]
                    ),
                    new PluginAdminBlock(
                        "form",
                        Title: "Change",
                        Text: "Leave a field empty to keep it. Placeholders like {player} are filled in from the event.",
                        Fields:
                        [
                            new PluginAdminField(
                                "webhook",
                                "Webhook (its name)",
                                Hint: "As named on the Webhooks page"
                            ),
                            new PluginAdminField("title", "Title", Hint: "At most 256 characters"),
                            new PluginAdminField("text", "Text", "textarea"),
                            new PluginAdminField(
                                "threshold",
                                "Threshold",
                                "number",
                                Min: 0,
                                Hint: "0: every one; for events that have one"
                            ),
                            new PluginAdminField(
                                "currency",
                                "Threshold currency",
                                "select",
                                Options: Currencies.NAMES
                            ),
                        ],
                        Submit: new PluginAdminAction("Save", "POST", "routes/{id}", Node: manage)
                    ),
                ],
                Nav: false
            )
        );
        admin.Page(
            new PluginAdminPage(
                "webhooks",
                "Discord webhooks",
                view,
                [
                    new PluginAdminBlock(
                        "table",
                        Endpoint: "webhooks",
                        Columns:
                        [
                            new PluginAdminColumn("name", "Name"),
                            new PluginAdminColumn("target", "Posts to"),
                            new PluginAdminColumn("events", "Events", "number"),
                            new PluginAdminColumn("waiting", "Waiting", "number"),
                            new PluginAdminColumn("lastOk", "Last post", "time"),
                            new PluginAdminColumn("lastError", "Last error"),
                        ],
                        ItemPage: "webhook"
                    ),
                    new PluginAdminBlock(
                        "form",
                        Title: "Add a webhook",
                        Text: "In Discord: the channel's settings, Integrations, Webhooks, New Webhook, Copy Webhook URL. The URL is a secret: it is never shown again here.",
                        Fields:
                        [
                            new PluginAdminField(
                                "name",
                                "Name",
                                Required: true,
                                Pattern: "[A-Za-z0-9_.-]{1,32}",
                                Hint: "Used in the routing table, e.g. staff-alerts"
                            ),
                            new PluginAdminField(
                                "url",
                                "Webhook URL",
                                Required: true,
                                Pattern: "https?://.+"
                            ),
                        ],
                        Submit: new PluginAdminAction("Add", "POST", "webhooks", Node: webhooks)
                    ),
                ],
                "The Discord channels the plugin posts to."
            )
        );
        admin.Page(
            new PluginAdminPage(
                "webhook",
                "Discord webhook",
                view,
                [
                    new PluginAdminBlock(
                        "detail",
                        Endpoint: "webhooks/{id}",
                        Columns:
                        [
                            new PluginAdminColumn("name", "Name"),
                            new PluginAdminColumn("target", "Posts to"),
                            new PluginAdminColumn("events", "Events"),
                            new PluginAdminColumn("waiting", "Waiting", "number"),
                            new PluginAdminColumn("createdAt", "Added", "time"),
                            new PluginAdminColumn("lastOk", "Last post", "time"),
                            new PluginAdminColumn("lastErrorAt", "Last error", "time"),
                            new PluginAdminColumn("lastError", "Error"),
                        ]
                    ),
                    new PluginAdminBlock(
                        "actions",
                        Actions:
                        [
                            new PluginAdminAction(
                                "Send a test post",
                                "POST",
                                "webhooks/{id}/test",
                                Node: webhooks
                            ),
                            new PluginAdminAction(
                                "Remove",
                                "DELETE",
                                "webhooks/{id}",
                                "Remove this webhook? Its events stop being posted and its waiting posts fail.",
                                Danger: true,
                                Node: webhooks,
                                Then: "back"
                            ),
                        ]
                    ),
                ],
                Nav: false
            )
        );
        admin.Page(
            new PluginAdminPage(
                "deliveries",
                "Discord deliveries",
                view,
                [
                    new PluginAdminBlock(
                        "table",
                        Endpoint: "deliveries",
                        Columns:
                        [
                            new PluginAdminColumn("createdAt", "Queued", "time"),
                            new PluginAdminColumn("title", "Post"),
                            new PluginAdminColumn("event", "Event"),
                            new PluginAdminColumn("webhook", "Webhook"),
                            new PluginAdminColumn("status", "Status", "badge"),
                            new PluginAdminColumn("attempts", "Tries", "number"),
                            new PluginAdminColumn("error", "Error"),
                        ],
                        Filters:
                        [
                            new PluginAdminField(
                                "status",
                                "Status",
                                "select",
                                Options:
                                [
                                    "",
                                    DeliveryEntity.QUEUED,
                                    DeliveryEntity.SENT,
                                    DeliveryEntity.FAILED,
                                    DeliveryEntity.DROPPED,
                                ]
                            ),
                            new PluginAdminField("q", "Event or post"),
                        ],
                        ItemPage: "delivery"
                    ),
                ],
                "What was posted to Discord, and what could not be.",
                Icon: "scroll"
            )
        );
        admin.Page(
            new PluginAdminPage(
                "delivery",
                "Discord delivery",
                view,
                [
                    new PluginAdminBlock(
                        "detail",
                        Endpoint: "deliveries/{id}",
                        Columns:
                        [
                            new PluginAdminColumn("title", "Post"),
                            new PluginAdminColumn("description", "Text"),
                            new PluginAdminColumn("event", "Event"),
                            new PluginAdminColumn("webhook", "Webhook"),
                            new PluginAdminColumn("status", "Status", "badge"),
                            new PluginAdminColumn("attempts", "Tries", "number"),
                            new PluginAdminColumn("createdAt", "Queued", "time"),
                            new PluginAdminColumn("nextAttemptAt", "Next try", "time"),
                            new PluginAdminColumn("doneAt", "Done", "time"),
                            new PluginAdminColumn("error", "Error"),
                            new PluginAdminColumn("dedupeKey", "Posted once by"),
                        ]
                    ),
                    new PluginAdminBlock(
                        "actions",
                        Actions:
                        [
                            new PluginAdminAction(
                                "Send again",
                                "POST",
                                "deliveries/{id}/retry",
                                Node: manage,
                                When: "retryable=true"
                            ),
                        ]
                    ),
                ],
                Nav: false
            )
        );
    }

    private async Task<T> WithDbAsync<T>(Func<DiscordDbContext, Task<T>> work)
    {
        await using var scope = scopes.CreateAsyncScope();

        return await work(scope.ServiceProvider.GetRequiredService<DiscordDbContext>())
            .ConfigureAwait(false);
    }

    private Task<PluginAdminResult> StatsAsync(CancellationToken ct) =>
        WithDbAsync(async db =>
        {
            var day = time.GetUtcNow().UtcDateTime.AddDays(-1);
            var counts = await db
                .Deliveries.GroupBy(x => x.Status)
                .Select(x => new
                {
                    Status = x.Key,
                    Count = x.Count(),
                    Day = x.Count(d => d.DoneAt >= day),
                })
                .ToListAsync(ct)
                .ConfigureAwait(false);
            int Count(string status, bool lastDay) =>
                counts.FirstOrDefault(x => x.Status == status) is { } c
                    ? (lastDay ? c.Day : c.Count)
                    : 0;
            var on = await db
                .Routes.CountAsync(x => x.Enabled && x.WebhookId != null, ct)
                .ConfigureAwait(false);
            var tiles = new List<object>
            {
                new { label = "Events on", value = on },
                new { label = "Waiting", value = Count(DeliveryEntity.QUEUED, false) },
                new { label = "Sent (24 h)", value = Count(DeliveryEntity.SENT, true) },
                new { label = "Failed (24 h)", value = Count(DeliveryEntity.FAILED, true) },
                new
                {
                    label = $"Dropped ({options.CurrentValue.DeliveryRetentionDays} days)",
                    value = Count(DeliveryEntity.DROPPED, false) + inbox.Dropped,
                },
            };

            if (!MovementsRecorded())
                tiles.Add(
                    new
                    {
                        label = "Asset movements",
                        value = "off: LTD, purchase and trade events need Turbo:Movements:Enabled",
                    }
                );

            return PluginAdminResult.Ok(tiles);
        });

    private bool MovementsRecorded() =>
        bool.TryParse(configuration?["Turbo:Movements:Enabled"], out var on) && on;

    // Webhooks

    /// <summary>
    /// What the panel shows of a webhook's URL: its host and webhook id, never its token. A URL that
    /// does not look like Discord's shows its host only.
    /// </summary>
    public static string Target(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return "(not a URL)";

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var at = Array.IndexOf(segments, "webhooks");

        return at >= 0 && at + 1 < segments.Length
            ? $"{uri.Host} webhook {segments[at + 1]}"
            : uri.Host;
    }

    private Task<PluginAdminResult> WebhooksAsync(CancellationToken ct) =>
        WithDbAsync(async db =>
        {
            var hooks = await db
                .Webhooks.AsNoTracking()
                .OrderBy(x => x.Name)
                .ToListAsync(ct)
                .ConfigureAwait(false);
            var events = await db
                .Routes.Where(x => x.WebhookId != null && x.Enabled)
                .GroupBy(x => x.WebhookId)
                .Select(x => new { Id = x.Key, Count = x.Count() })
                .ToDictionaryAsync(x => x.Id!.Value, x => x.Count, ct)
                .ConfigureAwait(false);
            var waiting = await db
                .Deliveries.Where(x => x.Status == DeliveryEntity.QUEUED)
                .GroupBy(x => x.WebhookId)
                .Select(x => new { Id = x.Key, Count = x.Count() })
                .ToDictionaryAsync(x => x.Id, x => x.Count, ct)
                .ConfigureAwait(false);

            return PluginAdminResult.Ok(
                new
                {
                    items = hooks.Select(x => new
                    {
                        id = x.Id,
                        name = x.Name,
                        target = Target(x.Url),
                        events = events.GetValueOrDefault(x.Id),
                        waiting = waiting.GetValueOrDefault(x.Id),
                        lastOk = x.LastOkAt,
                        lastError = x.LastError,
                    }),
                    total = hooks.Count,
                    pageSize = Math.Max(hooks.Count, PAGE_SIZE),
                }
            );
        });

    private Task<PluginAdminResult> WebhookAsync(
        PluginAdminRequest request,
        CancellationToken ct
    ) =>
        WithDbAsync(async db =>
        {
            if (await FindWebhookAsync(db, request, ct).ConfigureAwait(false) is not { } hook)
                return PluginAdminResult.NotFound("No such webhook.");

            var routed = await db
                .Routes.AsNoTracking()
                .Where(x => x.WebhookId == hook.Id)
                .Select(x => x.Key + (x.Enabled ? "" : " (off)"))
                .ToListAsync(ct)
                .ConfigureAwait(false);
            var waiting = await db
                .Deliveries.CountAsync(
                    x => x.WebhookId == hook.Id && x.Status == DeliveryEntity.QUEUED,
                    ct
                )
                .ConfigureAwait(false);

            return PluginAdminResult.Ok(
                new
                {
                    id = hook.Id,
                    name = hook.Name,
                    target = Target(hook.Url),
                    events = routed.Count == 0 ? "none" : string.Join(", ", routed.Order()),
                    waiting,
                    createdAt = hook.CreatedAt,
                    lastOk = hook.LastOkAt,
                    lastErrorAt = hook.LastErrorAt,
                    lastError = hook.LastError,
                }
            );
        });

    private Task<PluginAdminResult> AddWebhookAsync(
        PluginAdminRequest request,
        CancellationToken ct
    ) =>
        WithDbAsync(async db =>
        {
            var name = Text(request, "name")?.Trim() ?? "";
            var url = Text(request, "url")?.Trim() ?? "";

            if (
                name.Length is 0 or > WebhookEntity.MAX_NAME
                || !name.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.')
            )
                return PluginAdminResult.BadRequest(
                    "A name is 1 to 32 letters, digits, '-', '_' or '.'."
                );

            if (CheckUrl(url) is { } problem)
                return PluginAdminResult.BadRequest(problem);

            if (await db.Webhooks.AnyAsync(x => x.Name == name, ct).ConfigureAwait(false))
                return PluginAdminResult.BadRequest($"There is a webhook named {name} already.");

            var hook = new WebhookEntity
            {
                Name = name,
                Url = url,
                CreatedAt = time.GetUtcNow().UtcDateTime,
            };

            db.Webhooks.Add(hook);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);

            return PluginAdminResult.Ok(
                new
                {
                    id = hook.Id,
                    name,
                    target = Target(url),
                }
            );
        });

    /// <summary>Why <paramref name="url"/> is not a webhook the plugin posts to; null when it is.</summary>
    public string? CheckUrl(string url)
    {
        if (
            url.Length is 0 or > WebhookEntity.MAX_URL
            || !Uri.TryCreate(url, UriKind.Absolute, out var uri)
        )
            return "That is not a webhook URL.";

        if (!options.CurrentValue.AllowsHost(uri.Host))
            return $"Webhooks may post to {options.CurrentValue.AllowedHosts} only (setting AllowedHosts).";

        if (uri.Scheme != Uri.UriSchemeHttps && !uri.IsLoopback)
            return "A webhook URL is https.";

        if (!uri.AbsolutePath.Contains("/webhooks/", StringComparison.Ordinal))
            return "A Discord webhook URL has /api/webhooks/<id>/<token> in it.";

        return null;
    }

    private Task<PluginAdminResult> TestWebhookAsync(
        PluginAdminRequest request,
        CancellationToken ct
    ) =>
        WithDbAsync(async db =>
        {
            if (await FindWebhookAsync(db, request, ct).ConfigureAwait(false) is not { } hook)
                return PluginAdminResult.NotFound("No such webhook.");

            var config = options.CurrentValue;
            var now = time.GetUtcNow().UtcDateTime;

            if (!limiter.TryTake(hook.Id, now, config.MaxPerMinute))
                return PluginAdminResult.BadRequest(
                    $"This webhook is waiting on Discord's rate limit until {limiter.NextAt(hook.Id, now, config.MaxPerMinute):HH:mm:ss} UTC."
                );

            var embed = Embeds.Build(
                new DiscordEvent
                {
                    Key = "test",
                    OccurredAtUtc = now,
                    Values = new Dictionary<string, string> { ["name"] = hook.Name },
                },
                new EventInfo(
                    "test",
                    "Test",
                    "",
                    "Test post to {name}",
                    "The hotel's Discord plugin can post here.",
                    0x2ECC71,
                    []
                ),
                null,
                null,
                config.Footer
            );
            var result = await client
                .PostAsync(hook.Url, DiscordClient.Payload([embed], config), ct)
                .ConfigureAwait(false);

            if (result.RetryAfter is { } wait)
                limiter.Block(hook.Id, now, wait);

            if (result.Outcome == PostOutcome.Sent)
            {
                hook.LastOkAt = now;
                await db.SaveChangesAsync(ct).ConfigureAwait(false);

                return PluginAdminResult.Ok(new { sent = true });
            }

            hook.LastErrorAt = now;
            hook.LastError = Embeds.Cut(result.Error ?? "failed", DeliveryEntity.MAX_ERROR);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);

            return PluginAdminResult.BadRequest(result.Error ?? "Discord did not take the post.");
        });

    private Task<PluginAdminResult> RemoveWebhookAsync(
        PluginAdminRequest request,
        CancellationToken ct
    ) =>
        WithDbAsync(async db =>
        {
            if (await FindWebhookAsync(db, request, ct).ConfigureAwait(false) is not { } hook)
                return PluginAdminResult.NotFound("No such webhook.");

            var now = time.GetUtcNow().UtcDateTime;

            foreach (
                var route in await db
                    .Routes.Where(x => x.WebhookId == hook.Id)
                    .ToListAsync(ct)
                    .ConfigureAwait(false)
            )
            {
                route.WebhookId = null;
                route.UpdatedAt = now;
            }

            foreach (
                var row in await db
                    .Deliveries.Where(x =>
                        x.WebhookId == hook.Id && x.Status == DeliveryEntity.QUEUED
                    )
                    .ToListAsync(ct)
                    .ConfigureAwait(false)
            )
            {
                row.Status = DeliveryEntity.FAILED;
                row.DoneAt = now;
                row.Error = "The webhook was removed.";
            }

            db.Webhooks.Remove(hook);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            limiter.Forget(hook.Id);
            await switches.RefreshAsync(ct).ConfigureAwait(false);

            return PluginAdminResult.NoContent();
        });

    private static async Task<WebhookEntity?> FindWebhookAsync(
        DiscordDbContext db,
        PluginAdminRequest request,
        CancellationToken ct
    ) =>
        int.TryParse(request.Route.GetValueOrDefault("id"), out var id)
            ? await db.Webhooks.FirstOrDefaultAsync(x => x.Id == id, ct).ConfigureAwait(false)
            : null;

    // Routes

    /// <summary>A route row for every event the plugin knows, off, so the table lists them all.</summary>
    public async Task EnsureRoutesAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DiscordDbContext>();
        var known = (
            await db.Routes.Select(x => x.Key).ToListAsync(ct).ConfigureAwait(false)
        ).ToHashSet();
        var now = time.GetUtcNow().UtcDateTime;

        foreach (var info in EventCatalog.ALL.Where(x => !known.Contains(x.Key)))
            db.Routes.Add(
                new RouteEntity
                {
                    Key = info.Key,
                    Currency = info.Currency,
                    UpdatedAt = now,
                }
            );

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    private Task<PluginAdminResult> RoutesAsync(PluginAdminRequest request, CancellationToken ct) =>
        WithDbAsync(async db =>
        {
            var hooks = await db
                .Webhooks.AsNoTracking()
                .ToDictionaryAsync(x => x.Id, x => x.Name, ct)
                .ConfigureAwait(false);
            var routes = await db.Routes.AsNoTracking().ToListAsync(ct).ConfigureAwait(false);
            var order = EventCatalog
                .ALL.Select((x, i) => (x.Key, i))
                .ToDictionary(x => x.Key, x => x.i);
            var state = request.Query.GetValueOrDefault("state");
            var q = request.Query.GetValueOrDefault("q")?.Trim();
            var rows = routes
                .Select(x => (Route: x, Info: EventCatalog.Find(x.Key)))
                .Where(x => x.Info is not null)
                .Where(x =>
                    state switch
                    {
                        "on" => x.Route.Enabled,
                        "off" => !x.Route.Enabled,
                        _ => true,
                    }
                )
                .Where(x =>
                    string.IsNullOrEmpty(q)
                    || x.Info!.Label.Contains(q, StringComparison.OrdinalIgnoreCase)
                    || x.Route.Key.Contains(q, StringComparison.OrdinalIgnoreCase)
                )
                .OrderBy(x => order.GetValueOrDefault(x.Route.Key, int.MaxValue))
                .ThenBy(x => x.Route.Key)
                .ToList();
            var page = Page(request);

            return PluginAdminResult.Ok(
                new
                {
                    items = rows.Skip((page - 1) * PAGE_SIZE)
                        .Take(PAGE_SIZE)
                        .Select(x => new
                        {
                            id = x.Route.Id,
                            key = x.Route.Key,
                            @event = x.Info!.Label,
                            enabled = x.Route.Enabled,
                            webhook = x.Route.WebhookId is { } w
                                ? hooks.GetValueOrDefault(w, "?")
                                : "",
                            threshold = Threshold(x.Route, x.Info),
                            title = x.Route.Title ?? x.Info.Title,
                        }),
                    total = rows.Count,
                    pageSize = PAGE_SIZE,
                }
            );
        });

    private static string Threshold(RouteEntity route, EventInfo info) =>
        info.Threshold is null ? ""
        : route.Threshold <= 0 ? "every one"
        : info.Currency is null ? $"{info.Threshold.ToLowerInvariant()} {route.Threshold} items"
        : $"{info.Threshold.ToLowerInvariant()} {Currencies.Format(route.Threshold, route.Currency ?? info.Currency)}";

    private Task<PluginAdminResult> RouteAsync(PluginAdminRequest request, CancellationToken ct) =>
        WithDbAsync(async db =>
        {
            if (
                await FindRouteAsync(db, request, ct).ConfigureAwait(false) is not { } route
                || EventCatalog.Find(route.Key) is not { } info
            )
                return PluginAdminResult.NotFound("No such event.");

            var hook = route.WebhookId is { } w
                ? await db
                    .Webhooks.AsNoTracking()
                    .Where(x => x.Id == w)
                    .Select(x => x.Name)
                    .FirstOrDefaultAsync(ct)
                    .ConfigureAwait(false)
                : null;

            return PluginAdminResult.Ok(
                new
                {
                    id = route.Id,
                    key = route.Key,
                    @event = info.Label,
                    description = info.Description,
                    enabled = route.Enabled,
                    webhook = hook ?? "none: give it one below",
                    title = route.Title ?? info.Title,
                    text = route.Text ?? info.Text,
                    custom = route.Title is not null || route.Text is not null,
                    threshold = info.Threshold is null
                        ? "none for this event"
                        : Threshold(route, info),
                    placeholders = string.Join(" ", info.Placeholders.Select(x => $"{{{x}}}")),
                    updatedAt = route.UpdatedAt,
                }
            );
        });

    private Task<PluginAdminResult> EditRouteAsync(
        PluginAdminRequest request,
        CancellationToken ct
    ) =>
        WithDbAsync(async db =>
        {
            if (
                await FindRouteAsync(db, request, ct).ConfigureAwait(false) is not { } route
                || EventCatalog.Find(route.Key) is not { } info
            )
                return PluginAdminResult.NotFound("No such event.");

            if (Text(request, "webhook") is { Length: > 0 } name)
            {
                var hook = await db
                    .Webhooks.FirstOrDefaultAsync(x => x.Name == name.Trim(), ct)
                    .ConfigureAwait(false);

                if (hook is null)
                    return PluginAdminResult.BadRequest(
                        $"There is no webhook named {name}: add it on the Webhooks page."
                    );

                route.WebhookId = hook.Id;
            }

            if (Text(request, "title") is { Length: > 0 } title)
            {
                if (title.Length > RouteEntity.MAX_TITLE)
                    return PluginAdminResult.BadRequest("A title is at most 256 characters.");

                route.Title = title;
            }

            if (Text(request, "text") is { Length: > 0 } text)
            {
                if (text.Length > RouteEntity.MAX_TEXT)
                    return PluginAdminResult.BadRequest("A text is at most 2000 characters.");

                route.Text = text;
            }

            if (Number(request, "threshold") is { } threshold)
            {
                if (info.Threshold is null)
                    return PluginAdminResult.BadRequest("This event has no threshold.");

                if (threshold < 0)
                    return PluginAdminResult.BadRequest("A threshold is 0 or more.");

                route.Threshold = threshold;
            }

            if (Text(request, "currency") is { Length: > 0 } currency)
            {
                if (info.Currency is null)
                    return PluginAdminResult.BadRequest(
                        "This event's threshold counts items, not a currency."
                    );

                if (!Currencies.NAMES.Contains(currency))
                    return PluginAdminResult.BadRequest(
                        $"A currency is one of {string.Join(", ", Currencies.NAMES)}."
                    );

                route.Currency = currency;
            }

            route.UpdatedAt = time.GetUtcNow().UtcDateTime;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await switches.RefreshAsync(ct).ConfigureAwait(false);

            return PluginAdminResult.Ok(new { id = route.Id });
        });

    private Task<PluginAdminResult> SwitchAsync(
        PluginAdminRequest request,
        bool on,
        CancellationToken ct
    ) =>
        WithDbAsync(async db =>
        {
            if (await FindRouteAsync(db, request, ct).ConfigureAwait(false) is not { } route)
                return PluginAdminResult.NotFound("No such event.");

            if (on && route.WebhookId is null)
                return PluginAdminResult.BadRequest("Give the event a webhook first.");

            route.Enabled = on;
            route.UpdatedAt = time.GetUtcNow().UtcDateTime;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await switches.RefreshAsync(ct).ConfigureAwait(false);

            return PluginAdminResult.Ok(new { id = route.Id, enabled = on });
        });

    private Task<PluginAdminResult> ResetRouteAsync(
        PluginAdminRequest request,
        CancellationToken ct
    ) =>
        WithDbAsync(async db =>
        {
            if (await FindRouteAsync(db, request, ct).ConfigureAwait(false) is not { } route)
                return PluginAdminResult.NotFound("No such event.");

            route.Title = null;
            route.Text = null;
            route.UpdatedAt = time.GetUtcNow().UtcDateTime;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);

            return PluginAdminResult.Ok(new { id = route.Id });
        });

    private static async Task<RouteEntity?> FindRouteAsync(
        DiscordDbContext db,
        PluginAdminRequest request,
        CancellationToken ct
    ) =>
        int.TryParse(request.Route.GetValueOrDefault("id"), out var id)
            ? await db.Routes.FirstOrDefaultAsync(x => x.Id == id, ct).ConfigureAwait(false)
            : null;

    // Deliveries

    private Task<PluginAdminResult> DeliveriesAsync(
        PluginAdminRequest request,
        CancellationToken ct
    ) =>
        WithDbAsync(async db =>
        {
            var hooks = await db
                .Webhooks.AsNoTracking()
                .ToDictionaryAsync(x => x.Id, x => x.Name, ct)
                .ConfigureAwait(false);
            var query = db.Deliveries.AsNoTracking();

            if (request.Query.GetValueOrDefault("status") is { Length: > 0 } status)
                query = query.Where(x => x.Status == status);

            if (request.Query.GetValueOrDefault("q")?.Trim() is { Length: > 0 } q)
                query = query.Where(x => x.EventKey.Contains(q) || x.Title.Contains(q));

            var total = await query.CountAsync(ct).ConfigureAwait(false);
            var page = Page(request);
            var rows = await query
                .OrderByDescending(x => x.Id)
                .Skip((page - 1) * PAGE_SIZE)
                .Take(PAGE_SIZE)
                .ToListAsync(ct)
                .ConfigureAwait(false);

            return PluginAdminResult.Ok(
                new
                {
                    items = rows.Select(x => new
                    {
                        id = x.Id,
                        createdAt = x.CreatedAt,
                        title = x.Title,
                        @event = EventCatalog.Find(x.EventKey)?.Label ?? x.EventKey,
                        webhook = hooks.GetValueOrDefault(x.WebhookId, "(removed)"),
                        status = x.Status,
                        attempts = x.Attempts,
                        error = x.Error,
                    }),
                    total,
                    pageSize = PAGE_SIZE,
                }
            );
        });

    private Task<PluginAdminResult> DeliveryAsync(
        PluginAdminRequest request,
        CancellationToken ct
    ) =>
        WithDbAsync(async db =>
        {
            if (await FindDeliveryAsync(db, request, ct).ConfigureAwait(false) is not { } row)
                return PluginAdminResult.NotFound("No such delivery.");

            var hook = await db
                .Webhooks.AsNoTracking()
                .Where(x => x.Id == row.WebhookId)
                .Select(x => x.Name)
                .FirstOrDefaultAsync(ct)
                .ConfigureAwait(false);
            string? description = null;

            try
            {
                description = JsonDocument
                    .Parse(row.Embed)
                    .RootElement.TryGetProperty("description", out var d)
                    ? d.GetString()
                    : null;
            }
            catch (JsonException) { }

            return PluginAdminResult.Ok(
                new
                {
                    id = row.Id,
                    title = row.Title,
                    description,
                    @event = EventCatalog.Find(row.EventKey)?.Label ?? row.EventKey,
                    webhook = hook ?? "(removed)",
                    status = row.Status,
                    attempts = row.Attempts,
                    createdAt = row.CreatedAt,
                    nextAttemptAt = row.NextAttemptAt,
                    doneAt = row.DoneAt,
                    error = row.Error,
                    dedupeKey = row.DedupeKey,
                    retryable = row.Status == DeliveryEntity.FAILED && hook is not null,
                }
            );
        });

    private Task<PluginAdminResult> RetryAsync(PluginAdminRequest request, CancellationToken ct) =>
        WithDbAsync(async db =>
        {
            if (await FindDeliveryAsync(db, request, ct).ConfigureAwait(false) is not { } row)
                return PluginAdminResult.NotFound("No such delivery.");

            if (row.Status != DeliveryEntity.FAILED)
                return PluginAdminResult.BadRequest("Only a failed post is sent again.");

            if (!await db.Webhooks.AnyAsync(x => x.Id == row.WebhookId, ct).ConfigureAwait(false))
                return PluginAdminResult.BadRequest("Its webhook was removed.");

            row.Status = DeliveryEntity.QUEUED;
            row.Attempts = 0;
            row.NextAttemptAt = null;
            row.DoneAt = null;
            row.Error = null;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);

            return PluginAdminResult.Ok(new { id = row.Id, status = row.Status });
        });

    private static async Task<DeliveryEntity?> FindDeliveryAsync(
        DiscordDbContext db,
        PluginAdminRequest request,
        CancellationToken ct
    ) =>
        long.TryParse(request.Route.GetValueOrDefault("id"), out var id)
            ? await db.Deliveries.FirstOrDefaultAsync(x => x.Id == id, ct).ConfigureAwait(false)
            : null;

    private static int Page(PluginAdminRequest request) =>
        int.TryParse(request.Query.GetValueOrDefault("page"), out var page) && page > 0 ? page : 1;

    private static string? Text(PluginAdminRequest request, string name) =>
        request.Body is { ValueKind: JsonValueKind.Object } body
        && body.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static long? Number(PluginAdminRequest request, string name) =>
        request.Body is { ValueKind: JsonValueKind.Object } body
        && body.TryGetProperty(name, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.Number when value.TryGetInt64(out var n) => n,
                JsonValueKind.String
                    when long.TryParse(
                        value.GetString(),
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out var n
                    ) => n,
                _ => null,
            }
            : null;
}
