using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Turbo.Primitives.Movements;
using TurboDiscord.Database;
using TurboDiscord.Delivery;

namespace TurboDiscord.Sources;

/// <summary>
/// Reads core's asset movement outbox (<c>docs/movements.md</c>) for the events made from it (LTDs,
/// purchases, trades, sales, staff gifts). Each batch is one transaction: the posts it makes and
/// the cursor. Delivery is at least once, and each event is posted once per operation (its dedupe
/// key), so a batch delivered again, a hot reload or a restart never posts anything twice. Only
/// registered while one of those events is on: a hotel that posts none never reads the outbox.
/// </summary>
public sealed class MovementSource(
    IServiceScopeFactory scopes,
    Router router,
    INames names,
    IOptionsMonitor<DiscordConfig> options,
    TimeProvider time,
    ILogger<MovementSource> logger
) : IAssetMovementConsumer
{
    public const string NAME = "discord";

    public string Name => NAME;

    public async Task<long> GetCursorAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DiscordDbContext>();

        return await db
            .Cursors.AsNoTracking()
            .Where(x => x.Name == CursorEntity.MOVEMENTS)
            .Select(x => x.LastId)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task HandleAsync(AssetMovementBatch batch, CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var events = batch.Movements.IsDefaultOrEmpty
            ? []
            : await MovementEvents
                .ReadAsync(
                    batch.Movements.OrderBy(x => x.Id),
                    names,
                    now.AddMinutes(-Math.Max(options.CurrentValue.MaxEventAgeMinutes, 1)),
                    ct
                )
                .ConfigureAwait(false);

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DiscordDbContext>();

        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);

        var cursor = await db
            .Cursors.FirstOrDefaultAsync(x => x.Name == CursorEntity.MOVEMENTS, ct)
            .ConfigureAwait(false);

        if (cursor is null)
        {
            cursor = new CursorEntity { Name = CursorEntity.MOVEMENTS };
            db.Cursors.Add(cursor);
        }

        // A second dispatcher can be behind, and a late batch leaves the cursor where it was.
        cursor.LastId = Math.Max(cursor.LastId, batch.Cursor);
        cursor.UpdatedAt = now;

        var queued = await router.RouteAsync(db, events, ct).ConfigureAwait(false);

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);

        if (queued > 0 || batch.Gap is not null)
            logger.LogDebug(
                "Discord queued {Queued} posts from {Events} events up to outbox id {Cursor}{Gap}",
                queued,
                events.Count,
                batch.Cursor,
                batch.Gap is { } gap
                    ? $" (pruned before it was read: {gap.FirstId}-{gap.LastId})"
                    : ""
            );
    }
}
