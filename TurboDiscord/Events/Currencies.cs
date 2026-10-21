using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Turbo.Primitives.Players.Enums.Wallet;
using Turbo.Primitives.Players.Wallet;

namespace TurboDiscord.Events;

/// <summary>A currency's name in routes and posts: credits, duckets (points 0), diamonds (points 5).</summary>
public static class Currencies
{
    public const string CREDITS = "credits";

    /// <summary>The names a route's threshold may count in, for the panel's select.</summary>
    public static readonly IReadOnlyList<string> NAMES =
    [
        CREDITS,
        "duckets",
        "diamonds",
        "silver",
        "emeralds",
    ];

    public static string Name(CurrencyKind kind) =>
        kind.CurrencyType switch
        {
            CurrencyType.Credits => CREDITS,
            CurrencyType.Silver => "silver",
            CurrencyType.Emeralds => "emeralds",
            CurrencyType.ActivityPoints => kind.ActivityPointType switch
            {
                null or 0 => "duckets",
                5 => "diamonds",
                var type => $"points {type}",
            },
            _ => kind.CurrencyType.ToString().ToLowerInvariant(),
        };

    /// <summary><c>1,500 credits + 20 diamonds</c>; <c>nothing</c> for none.</summary>
    public static string Format(IReadOnlyDictionary<string, long> amounts)
    {
        var parts = amounts
            .Where(x => x.Key != DiscordEvent.ITEMS && x.Value != 0)
            .Select(x => Format(x.Value, x.Key))
            .ToList();

        return parts.Count == 0 ? "nothing" : string.Join(" + ", parts);
    }

    public static string Format(long amount, string currency) =>
        $"{amount.ToString("N0", CultureInfo.InvariantCulture)} {currency}";
}
