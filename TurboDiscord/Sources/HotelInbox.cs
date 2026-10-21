using System.Threading;
using System.Threading.Channels;
using Turbo.Primitives.Events;
using TurboDiscord.Events;

namespace TurboDiscord.Sources;

/// <summary>
/// The hotel's events as they are heard (a report, a ban, a login, a plugin's notice, a countdown),
/// waiting for <see cref="HotelEvents"/> to look up what they name and route them. Handlers only
/// write here: the hotel never waits for a lookup, the database or Discord. Bounded: past
/// <see cref="CAPACITY"/> the oldest waiting is dropped and counted.
/// </summary>
public sealed class HotelInbox
{
    public const int CAPACITY = 1000;

    private readonly Channel<object> _channel;
    private long _dropped;

    public HotelInbox()
    {
        _channel = Channel.CreateBounded<object>(
            new BoundedChannelOptions(CAPACITY)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
            },
            _ => Interlocked.Increment(ref _dropped)
        );
    }

    /// <summary>How many events a full inbox dropped since the plugin loaded.</summary>
    public long Dropped => Interlocked.Read(ref _dropped);

    public ChannelReader<object> Reader => _channel.Reader;

    /// <summary>A hotel event to look into.</summary>
    public void Add(IEvent evt) => _channel.Writer.TryWrite(evt);

    /// <summary>An event that needs no lookup.</summary>
    public void Add(DiscordEvent evt) => _channel.Writer.TryWrite(evt);
}
