using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TurboDiscord.Database;
using TurboDiscord.Events;

namespace TurboDiscord.Sources;

/// <summary>
/// Which events are on, in memory: a hotel event whose route is off is dropped where it is heard,
/// before any lookup, so a busy hotel's logins cost nothing while their route is off. Read again
/// when a route changes on the panel, and every minute.
/// </summary>
public sealed class RouteSwitches(IServiceScopeFactory scopes)
{
    private FrozenSet<string> _on = FrozenSet<string>.Empty;

    /// <summary>Raised after the switches were read again.</summary>
    public event Action? Changed;

    public bool IsOn(string key) => _on.Contains(key);

    /// <summary>Whether any event read from asset movements is on.</summary>
    public bool AnyFromMovements =>
        EventCatalog.ALL.Any(x => x.FromMovements && _on.Contains(x.Key));

    /// <summary>Whether plugin notices may be posted: any is on, or a new topic should get its route.</summary>
    public bool IsOnOrNew(string key) => _on.Contains(key) || !_known.Contains(key);

    private FrozenSet<string> _known = FrozenSet<string>.Empty;

    public async Task RefreshAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DiscordDbContext>();
        var routes = await db
            .Routes.AsNoTracking()
            .Select(x => new { x.Key, On = x.Enabled && x.WebhookId != null })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        _on = routes.Where(x => x.On).Select(x => x.Key).ToFrozenSet();
        _known = routes.Select(x => x.Key).ToFrozenSet();
        Changed?.Invoke();
    }

    /// <summary>For tests: the keys on.</summary>
    internal void Set(IEnumerable<string> on, IEnumerable<string>? known = null)
    {
        _on = on.ToFrozenSet();
        _known = (known ?? on).ToFrozenSet();
    }
}
