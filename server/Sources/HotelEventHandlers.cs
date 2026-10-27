using System.Threading;
using System.Threading.Tasks;
using Turbo.Events.Registry;
using Turbo.Primitives.Moderation.Events;
using Turbo.Primitives.Players.Events;
using Turbo.Primitives.Plugins.Events;
using TurboDiscord.Events;

namespace TurboDiscord.Sources;

// The hotel's events, heard by the plugin's assembly scan. Each only checks its route is on and
// hands the event to the inbox: the hotel is waiting (a login, a report, another plugin).

public sealed class CallForHelpHandler(RouteSwitches switches, HotelInbox inbox)
    : IEventHandler<CallForHelpChangedEvent>
{
    public ValueTask HandleAsync(
        CallForHelpChangedEvent env,
        EventContext ctx,
        CancellationToken ct
    )
    {
        if (switches.IsOn(EventCatalog.REPORT_NEW))
            inbox.Add(env);

        return ValueTask.CompletedTask;
    }
}

public sealed class SanctionHandler(RouteSwitches switches, HotelInbox inbox)
    : IEventHandler<PlayerSanctionChangedEvent>
{
    public ValueTask HandleAsync(
        PlayerSanctionChangedEvent env,
        EventContext ctx,
        CancellationToken ct
    )
    {
        if (
            switches.IsOn(EventCatalog.PLAYER_BANNED) || switches.IsOn(EventCatalog.PLAYER_UNBANNED)
        )
            inbox.Add(env);

        return ValueTask.CompletedTask;
    }
}

public sealed class OnlineHandler(RouteSwitches switches, HotelInbox inbox)
    : IEventHandler<PlayerOnlineChangedEvent>
{
    public ValueTask HandleAsync(
        PlayerOnlineChangedEvent env,
        EventContext ctx,
        CancellationToken ct
    )
    {
        if (switches.IsOn(env.Online ? EventCatalog.PLAYER_LOGIN : EventCatalog.PLAYER_LOGOUT))
            inbox.Add(env);

        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Another plugin's notice (<c>docs/plugin-notices.md</c> in turbo-cloud). A topic heard for the
/// first time is queued too, so it gets its route (off) and shows in the routing table.
/// </summary>
public sealed class PluginNoticeHandler(RouteSwitches switches, HotelInbox inbox)
    : IEventHandler<PluginNoticeEvent>
{
    public ValueTask HandleAsync(PluginNoticeEvent env, EventContext ctx, CancellationToken ct)
    {
        if (
            !string.IsNullOrWhiteSpace(env.Source)
            && !string.IsNullOrWhiteSpace(env.Topic)
            && switches.IsOnOrNew(EventCatalog.PluginKey(env.Source, env.Topic))
        )
            inbox.Add(env);

        return ValueTask.CompletedTask;
    }
}
