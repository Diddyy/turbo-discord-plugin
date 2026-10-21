using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Turbo.Primitives.Moderation;
using Turbo.Primitives.Moderation.Events;
using Turbo.Primitives.Moderation.Snapshots;
using Turbo.Primitives.Players.Events;
using Turbo.Primitives.Plugins.Events;
using TurboDiscord.Database;
using TurboDiscord.Delivery;
using TurboDiscord.Events;

namespace TurboDiscord.Sources;

/// <summary>
/// Turns what <see cref="HotelInbox"/> heard into posts: looks up the report, the ban, the names,
/// and routes the events in batches. A report is new when it is open and younger than
/// <see cref="NEW_REPORT"/>: the same event is raised when a report is picked, let go or closed.
/// Also reads the routes' switches again every minute.
/// </summary>
public sealed class HotelEvents(
    HotelInbox inbox,
    IServiceScopeFactory scopes,
    Router router,
    RouteSwitches switches,
    INames names,
    IModerationIssueService issues,
    ISanctionService sanctions,
    TimeProvider time,
    ILogger<HotelEvents> logger
)
{
    public static readonly TimeSpan NEW_REPORT = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan SWITCHES_EVERY = TimeSpan.FromMinutes(1);
    private const int BATCH = 50;

    private CancellationTokenSource? _stop;
    private Task? _loop;

    public void Start()
    {
        _stop = new CancellationTokenSource();
        _loop = Task.Run(() => RunAsync(_stop.Token));
    }

    public async Task StopAsync()
    {
        if (_stop is null || _loop is null)
            return;

        await _stop.CancelAsync().ConfigureAwait(false);

        try
        {
            await _loop.ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }

        // What is still waiting (the server stopping) is routed in a moment at most.
        using var drain = new CancellationTokenSource(TimeSpan.FromSeconds(3));

        try
        {
            await DrainAsync(drain.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            logger.LogDebug(ex, "Hotel events left unrouted at stop");
        }

        _stop.Dispose();
        _stop = null;
        _loop = null;
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var nextSwitches = DateTime.MinValue;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var tick = CancellationTokenSource.CreateLinkedTokenSource(ct);

                tick.CancelAfter(SWITCHES_EVERY);

                try
                {
                    await inbox.Reader.WaitToReadAsync(tick.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }

                await DrainAsync(ct).ConfigureAwait(false);

                var now = time.GetUtcNow().UtcDateTime;

                if (now >= nextSwitches)
                {
                    nextSwitches = now + SWITCHES_EVERY;
                    await switches.RefreshAsync(ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Discord could not route hotel events; going on");
            }
        }
    }

    /// <summary>Routes everything waiting in the inbox; how many posts were queued.</summary>
    public async Task<int> DrainAsync(CancellationToken ct)
    {
        var queued = 0;

        while (true)
        {
            var heard = new List<object>();

            while (heard.Count < BATCH && inbox.Reader.TryRead(out var item))
                heard.Add(item);

            if (heard.Count == 0)
                return queued;

            var events = new List<DiscordEvent>();
            var newTopic = false;

            foreach (var item in heard)
            {
                try
                {
                    events.AddRange(await ReadAsync(item, ct).ConfigureAwait(false));
                    newTopic |=
                        item is PluginNoticeEvent n
                        && !switches.IsOn(EventCatalog.PluginKey(n.Source, n.Topic));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Discord could not read a {Event}", item.GetType().Name);
                }
            }

            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<DiscordDbContext>();

            queued += await router.RouteAsync(db, events, ct).ConfigureAwait(false);

            if (newTopic)
                await switches.RefreshAsync(ct).ConfigureAwait(false);
        }
    }

    private async Task<IEnumerable<DiscordEvent>> ReadAsync(object item, CancellationToken ct) =>
        item switch
        {
            DiscordEvent evt => [evt],
            CallForHelpChangedEvent cfh => await ReportsAsync(cfh, ct).ConfigureAwait(false),
            PlayerSanctionChangedEvent sanction =>
            [
                await SanctionAsync(sanction, ct).ConfigureAwait(false),
            ],
            PlayerOnlineChangedEvent online =>
            [
                await OnlineAsync(online, ct).ConfigureAwait(false),
            ],
            PluginNoticeEvent notice => [Notice(notice)],
            _ => [],
        };

    private async Task<IEnumerable<DiscordEvent>> ReportsAsync(
        CallForHelpChangedEvent cfh,
        CancellationToken ct
    )
    {
        var open = await issues.GetOpenIssuesAsync(ct).ConfigureAwait(false);
        var now = time.GetUtcNow().UtcDateTime;

        return open.Where(x =>
                cfh.IssueIds.Contains(x.IssueId)
                && x.State == ModIssueSnapshot.STATE_OPEN
                && x.IssueAgeInMilliseconds < NEW_REPORT.TotalMilliseconds
            )
            .Select(x => new DiscordEvent
            {
                Key = EventCatalog.REPORT_NEW,
                DedupeKey = $"report:{x.IssueId}",
                OccurredAtUtc = now.AddMilliseconds(-x.IssueAgeInMilliseconds),
                Values = new Dictionary<string, string>
                {
                    ["reporter"] = Name(x.ReporterUserName, x.ReporterUserId),
                    ["reported"] =
                        x.ReportedUserId > 0
                            ? Name(x.ReportedUserName, x.ReportedUserId)
                            : "no one",
                    ["message"] = x.Message,
                    ["category"] = x.CategoryId.ToString(CultureInfo.InvariantCulture),
                    ["id"] = x.IssueId.ToString(CultureInfo.InvariantCulture),
                },
                Fields = [new("Report", $"#{x.IssueId}"), new("Topic", x.CategoryId.ToString())],
                Url = names.Panel($"reports/{x.IssueId}"),
            })
            .ToList();
    }

    private async Task<DiscordEvent> SanctionAsync(
        PlayerSanctionChangedEvent sanction,
        CancellationToken ct
    )
    {
        var ban = await sanctions.GetActiveBanAsync(sanction.PlayerId, ct).ConfigureAwait(false);
        var player = sanction.PlayerId.Value;
        var ids = new List<int> { player };

        if (ban?.IssuerId is { } issuer)
            ids.Add(issuer.Value);

        var known = await names.PlayersAsync(ids, ct).ConfigureAwait(false);
        var now = time.GetUtcNow().UtcDateTime;

        if (ban is null)
            return new DiscordEvent
            {
                Key = EventCatalog.PLAYER_UNBANNED,
                OccurredAtUtc = now,
                Values = new Dictionary<string, string> { ["player"] = known[player] },
                Url = names.Panel($"players/{player}"),
            };

        var by = ban.IssuerId is { } id ? known[id.Value] : "the console";
        var until = ban.ExpiresAtUtc is { } expires
            ? expires.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture)
            : "never";

        return new DiscordEvent
        {
            Key = EventCatalog.PLAYER_BANNED,
            DedupeKey = $"ban:{ban.Id}",
            OccurredAtUtc = ban.IssuedAtUtc,
            Values = new Dictionary<string, string>
            {
                ["player"] = known[player],
                ["reason"] = ban.Reason,
                ["by"] = by,
                ["until"] = until,
            },
            Fields = [new("By", by), new("Until", until)],
            Url = names.Panel($"players/{player}"),
        };
    }

    private async Task<DiscordEvent> OnlineAsync(
        PlayerOnlineChangedEvent online,
        CancellationToken ct
    )
    {
        var player = online.PlayerId.Value;
        var known = await names.PlayersAsync([player], ct).ConfigureAwait(false);

        return new DiscordEvent
        {
            Key = online.Online ? EventCatalog.PLAYER_LOGIN : EventCatalog.PLAYER_LOGOUT,
            OccurredAtUtc = time.GetUtcNow().UtcDateTime,
            Values = new Dictionary<string, string> { ["player"] = known[player] },
            Url = names.Panel($"players/{player}"),
        };
    }

    /// <summary>A plugin's notice as it is: its title, text, fields and link, coloured by its level.</summary>
    public DiscordEvent Notice(PluginNoticeEvent notice)
    {
        var key = EventCatalog.PluginKey(notice.Source, notice.Topic);

        return new DiscordEvent
        {
            Key = Embeds.Cut(key, RouteEntity.MAX_KEY),
            DedupeKey = notice.Key is { Length: > 0 } k ? $"{key}:{k}" : null,
            OccurredAtUtc = time.GetUtcNow().UtcDateTime,
            Values = new Dictionary<string, string>
            {
                ["title"] = notice.Title,
                ["text"] = notice.Text ?? string.Empty,
                ["source"] = notice.Source,
                ["topic"] = notice.Topic,
            },
            Fields = notice.Fields.IsDefaultOrEmpty
                ? []
                :
                [
                    .. notice
                        .Fields.Take(PluginNoticeEvent.MAX_FIELDS)
                        .Select(x => new EventField(x.Name, x.Value)),
                ],
            Url = notice.Url,
            Colour = notice.Level switch
            {
                PluginNoticeLevel.Success => 0x2ECC71,
                PluginNoticeLevel.Warning => 0xE67E22,
                PluginNoticeLevel.Error => 0xE74C3C,
                _ => 0x3498DB,
            },
        };
    }

    private static string Name(string name, int id) => string.IsNullOrEmpty(name) ? $"#{id}" : name;
}
