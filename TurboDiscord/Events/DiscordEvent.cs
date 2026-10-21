using System;
using System.Collections.Generic;

namespace TurboDiscord.Events;

/// <summary>
/// Something that may be posted: its event key (<see cref="EventCatalog"/>, or a plugin notice's),
/// the values its title and text are written from, the fields shown under them, a link, what it
/// is worth for a route's threshold, and the key it is posted once by.
/// </summary>
public sealed record DiscordEvent
{
    public required string Key { get; init; }

    /// <summary>The thing it is about (<c>mv:&lt;operation&gt;</c>, <c>report:12</c>): posted once per key. Null: every one.</summary>
    public string? DedupeKey { get; init; }

    public required DateTime OccurredAtUtc { get; init; }

    /// <summary>The placeholders' values, unescaped: <c>{player}</c> is <c>Values["player"]</c>.</summary>
    public IReadOnlyDictionary<string, string> Values { get; init; } =
        new Dictionary<string, string>();

    public IReadOnlyList<EventField> Fields { get; init; } = [];

    /// <summary>Where the embed's title links to (an admin panel page); null: nowhere.</summary>
    public string? Url { get; init; }

    /// <summary>
    /// What it is worth, by currency (<see cref="Currencies"/>), or under <see cref="ITEMS"/> for a
    /// count of items: a route posts it when the worth in its currency reaches its threshold.
    /// </summary>
    public IReadOnlyDictionary<string, long> Worth { get; init; } = new Dictionary<string, long>();

    /// <summary>The colour the event's own colour gives way to (a plugin notice's level).</summary>
    public int? Colour { get; init; }

    public const string ITEMS = "items";
}

/// <summary>A field under an embed's text: a name and a value (written as is, escaped).</summary>
public readonly record struct EventField(string Name, string Value, bool Inline = true);
