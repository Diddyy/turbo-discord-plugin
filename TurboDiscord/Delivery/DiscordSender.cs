using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TurboDiscord.Database;

namespace TurboDiscord.Delivery;

/// <summary>
/// The plugin's one sending loop. For each webhook it takes the oldest waiting posts that are due,
/// once the first has waited <see cref="DiscordConfig.BatchWindowMs"/> or ten are waiting, packs
/// up to ten embeds (and Discord's 6000 characters) into one message, and posts it when the
/// webhook's pace allows (<see cref="WebhookLimiter"/>). Taken: the posts are sent. A 429: the
/// webhook waits what Discord said, the posts stay as they are. Discord down: each post is tried
/// again later, ever further apart, up to <see cref="DiscordConfig.MaxAttempts"/>. Refused: failed
/// with Discord's answer (a message of several is split, so one bad embed fails alone). A webhook
/// Discord no longer knows fails its posts. Sent, failed and dropped posts are kept
/// <see cref="DiscordConfig.DeliveryRetentionDays"/>.
/// </summary>
public sealed class DiscordSender(
    IServiceScopeFactory scopes,
    IOptionsMonitor<DiscordConfig> options,
    DiscordClient client,
    WebhookLimiter limiter,
    TimeProvider time,
    ILogger<DiscordSender> logger
)
{
    public static readonly TimeSpan TICK = TimeSpan.FromMilliseconds(500);
    public static readonly TimeSpan RETENTION_EVERY = TimeSpan.FromHours(1);
    public static readonly TimeSpan MAX_BACKOFF = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan FLUSH_ON_STOP = TimeSpan.FromSeconds(5);

    private readonly SemaphoreSlim _wake = new(0, 1);
    private CancellationTokenSource? _stop;
    private Task? _loop;
    private DateTime _nextRetention = DateTime.MinValue;

    public void Start()
    {
        _stop = new CancellationTokenSource();
        _loop = Task.Run(() => RunAsync(_stop.Token));
    }

    /// <summary>Looks at the queue now rather than at the next tick.</summary>
    public void Wake()
    {
        try
        {
            if (_wake.CurrentCount == 0)
                _wake.Release();
        }
        catch (SemaphoreFullException) { }
    }

    /// <summary>Stops the loop, then posts what is due without waiting for a batch (a few seconds at most).</summary>
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

        using var flush = new CancellationTokenSource(FLUSH_ON_STOP);

        try
        {
            await SendDueAsync(flush: true, flush.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            logger.LogDebug(ex, "Discord posts left waiting at stop");
        }

        _stop.Dispose();
        _stop = null;
        _loop = null;
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _wake.WaitAsync(TICK, ct).ConfigureAwait(false);
                await SendDueAsync(flush: false, ct).ConfigureAwait(false);
                await RetainAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Discord sending failed; trying again");
            }
        }
    }

    /// <summary>One pass over every webhook; how many messages were posted.</summary>
    public async Task<int> SendDueAsync(bool flush, CancellationToken ct)
    {
        var config = options.CurrentValue;

        if (!config.Enabled)
            return 0;

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DiscordDbContext>();
        var now = time.GetUtcNow().UtcDateTime;
        var webhooks = await db.Webhooks.ToListAsync(ct).ConfigureAwait(false);
        var posted = 0;

        foreach (var webhook in webhooks)
        {
            var due = await db
                .Deliveries.Where(x =>
                    x.Status == DeliveryEntity.QUEUED
                    && x.WebhookId == webhook.Id
                    && (x.NextAttemptAt == null || x.NextAttemptAt <= now)
                )
                .OrderBy(x => x.Id)
                .Take(Events.Embeds.MAX_EMBEDS)
                .ToListAsync(ct)
                .ConfigureAwait(false);

            if (due.Count == 0)
                continue;

            // A burst is one message: the first post waits for the others a moment.
            var waited = now - due[0].CreatedAt >= TimeSpan.FromMilliseconds(config.BatchWindowMs);

            if (!flush && !waited && due.Count < Events.Embeds.MAX_EMBEDS && due[0].Attempts == 0)
                continue;

            posted += await PostAsync(db, webhook, Pack(due), config, now, ct)
                .ConfigureAwait(false);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        return posted;
    }

    /// <summary>The oldest posts that fit in one message: ten embeds, 6000 characters.</summary>
    internal static List<DeliveryEntity> Pack(List<DeliveryEntity> due)
    {
        var batch = new List<DeliveryEntity>();
        var characters = 0;

        foreach (var row in due)
        {
            var size = Events.Embeds.Characters(Embed(row));

            if (batch.Count > 0 && characters + size > Events.Embeds.MAX_MESSAGE_CHARACTERS)
                break;

            batch.Add(row);
            characters += size;
        }

        return batch;
    }

    private async Task<int> PostAsync(
        DiscordDbContext db,
        WebhookEntity webhook,
        List<DeliveryEntity> batch,
        DiscordConfig config,
        DateTime now,
        CancellationToken ct
    )
    {
        if (!limiter.TryTake(webhook.Id, now, config.MaxPerMinute))
            return 0;

        var result = await client
            .PostAsync(webhook.Url, DiscordClient.Payload(batch.Select(Embed), config), ct)
            .ConfigureAwait(false);

        if (result.RetryAfter is { } wait)
            limiter.Block(webhook.Id, time.GetUtcNow().UtcDateTime, wait);

        switch (result.Outcome)
        {
            case PostOutcome.Sent:
                foreach (var row in batch)
                {
                    row.Status = DeliveryEntity.SENT;
                    row.Attempts++;
                    row.DoneAt = now;
                    row.Error = null;
                }

                webhook.LastOkAt = now;

                return 1;

            case PostOutcome.RateLimited:
                logger.LogInformation(
                    "Discord webhook {Webhook} is rate limited for {Seconds:N1} s",
                    webhook.Name,
                    result.RetryAfter?.TotalSeconds
                );

                return 0;

            case PostOutcome.Rejected when batch.Count > 1:
                // Which embed Discord refused is not said: each goes alone next, and fails alone.
                foreach (var row in batch)
                    row.Attempts = Math.Max(row.Attempts, 1);

                var posted = 0;

                foreach (var row in batch)
                    posted += await PostAsync(db, webhook, [row], config, now, ct)
                        .ConfigureAwait(false);

                return posted;

            case PostOutcome.Retry:
                foreach (var row in batch)
                {
                    row.Attempts++;
                    row.Error = Cut(result.Error);

                    if (row.Attempts >= Math.Max(config.MaxAttempts, 1))
                        Fail(row, now, result.Error);
                    else
                        row.NextAttemptAt = now + Backoff(row.Attempts);
                }

                Note(webhook, now, result.Error);

                return 0;

            default:
                foreach (var row in batch)
                {
                    row.Attempts++;
                    Fail(row, now, result.Error);
                }

                Note(webhook, now, result.Error);

                return 0;
        }
    }

    /// <summary>2 s, 4 s, 8 s... up to <see cref="MAX_BACKOFF"/>.</summary>
    public static TimeSpan Backoff(int attempts) =>
        TimeSpan.FromSeconds(
            Math.Min(Math.Pow(2, Math.Clamp(attempts, 1, 20)), MAX_BACKOFF.TotalSeconds)
        );

    private static void Fail(DeliveryEntity row, DateTime now, string? error)
    {
        row.Status = DeliveryEntity.FAILED;
        row.DoneAt = now;
        row.Error = Cut(error);
    }

    private static void Note(WebhookEntity webhook, DateTime now, string? error)
    {
        webhook.LastErrorAt = now;
        webhook.LastError = Cut(error);
    }

    private static string? Cut(string? error) =>
        error is null ? null : Events.Embeds.Cut(error, DeliveryEntity.MAX_ERROR);

    private static JsonObject Embed(DeliveryEntity row) =>
        JsonNode.Parse(row.Embed) as JsonObject ?? [];

    private async Task RetainAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;

        if (now < _nextRetention)
            return;

        _nextRetention = now + RETENTION_EVERY;

        var before = now.AddDays(-Math.Max(options.CurrentValue.DeliveryRetentionDays, 1));

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DiscordDbContext>();

        await db
            .Deliveries.Where(x => x.Status != DeliveryEntity.QUEUED && x.DoneAt < before)
            .ExecuteDeleteAsync(ct)
            .ConfigureAwait(false);
    }
}
