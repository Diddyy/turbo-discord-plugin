using System.Collections.Generic;
using System.Linq;

namespace TurboDiscord.Events;

/// <summary>
/// An event the plugin can post: its key, what the routing table calls it, the title and text it
/// is written with until the hotel changes them, its colour, the placeholders it fills, and what
/// its threshold counts (null: it has none). Every event is off until a hotel turns it on.
/// </summary>
public sealed record EventInfo(
    string Key,
    string Label,
    string Description,
    string Title,
    string Text,
    int Colour,
    IReadOnlyList<string> Placeholders,
    string? Threshold = null,
    string? Currency = null,
    bool FromMovements = false
);

/// <summary>The events the plugin knows; a plugin's notice topics are routed as <c>plugin:&lt;source&gt;:&lt;topic&gt;</c>.</summary>
public static class EventCatalog
{
    public const string LTD_PURCHASE = "ltd.purchase";
    public const string CATALOG_PURCHASE = "catalog.purchase";
    public const string TRADE = "trade";
    public const string MARKET_BUY = "market.buy";
    public const string STAFF_GIVE_CURRENCY = "staff.give_currency";
    public const string STAFF_GIVE_ITEM = "staff.give_item";
    public const string REWARD_TRACK = "reward.track";
    public const string REPORT_NEW = "report.new";
    public const string PLAYER_BANNED = "player.banned";
    public const string PLAYER_UNBANNED = "player.unbanned";
    public const string PLAYER_LOGIN = "player.login";
    public const string PLAYER_LOGOUT = "player.logout";
    public const string SERVER_STARTED = "server.started";
    public const string SERVER_STOPPING = "server.stopping";
    public const string HOTEL_COUNTDOWN = "hotel.countdown";
    public const string HOTEL_COUNTDOWN_CANCELLED = "hotel.countdown_cancelled";
    public const string HOTEL_MAINTENANCE = "hotel.maintenance";

    public const string PLUGIN_PREFIX = "plugin:";

    private const int GOLD = 0xF1C40F;
    private const int GREEN = 0x2ECC71;
    private const int BLUE = 0x3498DB;
    private const int PURPLE = 0x9B59B6;
    private const int ORANGE = 0xE67E22;
    private const int RED = 0xE74C3C;
    private const int GREY = 0x95A5A6;

    public static readonly IReadOnlyList<EventInfo> ALL =
    [
        new(
            LTD_PURCHASE,
            "LTD bought",
            "A limited edition furni bought or won in its raffle.",
            "{player} got {furni} #{serial}/{series}",
            "Paid {price}.",
            GOLD,
            ["player", "furni", "serial", "series", "price"],
            FromMovements: true
        ),
        new(
            CATALOG_PURCHASE,
            "Catalogue purchase",
            "A purchase from the catalogue (not an LTD), over the threshold in its currency.",
            "{player} spent {price}",
            "{items} × {furni}",
            BLUE,
            ["player", "furni", "items", "price"],
            "Spent at least",
            Currencies.CREDITS,
            FromMovements: true
        ),
        new(
            TRADE,
            "Trade",
            "A completed trade, with at least the threshold's number of items in it.",
            "{player} traded with {partner}",
            "**{player}** gave {given}\n**{partner}** gave {received}",
            PURPLE,
            ["player", "partner", "given", "received", "items"],
            "Items at least",
            FromMovements: true
        ),
        new(
            MARKET_BUY,
            "Marketplace sale",
            "A furni bought on the marketplace, for at least the threshold.",
            "{player} bought {furni} on the marketplace",
            "From {seller} for {price}.",
            GREEN,
            ["player", "seller", "furni", "price"],
            "Price at least",
            Currencies.CREDITS,
            FromMovements: true
        ),
        new(
            STAFF_GIVE_CURRENCY,
            "Staff gave currency",
            "A staff member gave (or took) currency with :give, at least the threshold.",
            "{staff} {gave} {player} {amount}",
            "",
            ORANGE,
            ["staff", "gave", "player", "amount"],
            "Amount at least",
            Currencies.CREDITS,
            FromMovements: true
        ),
        new(
            STAFF_GIVE_ITEM,
            "Staff gave furni",
            "A staff member gave furni with :giveitem or :gift.",
            "{staff} gave {player} {furni}",
            "",
            ORANGE,
            ["staff", "player", "furni", "items"],
            FromMovements: true
        ),
        new(
            REWARD_TRACK,
            "Reward track prize",
            "A prize of a reward track claimed.",
            "{player} claimed {prize}",
            "",
            GREEN,
            ["player", "prize"],
            FromMovements: true
        ),
        new(
            REPORT_NEW,
            "New call for help",
            "A player sent a report to the moderators.",
            "{reporter} reported {reported}",
            "{message}",
            RED,
            ["reporter", "reported", "message", "category", "id"]
        ),
        new(
            PLAYER_BANNED,
            "Player banned",
            "A player was banned from the hotel.",
            "{player} was banned",
            "{reason}",
            RED,
            ["player", "reason", "by", "until"]
        ),
        new(
            PLAYER_UNBANNED,
            "Ban lifted",
            "A player's ban was lifted (not one that ran out).",
            "{player}'s ban was lifted",
            "",
            GREY,
            ["player"]
        ),
        new(
            PLAYER_LOGIN,
            "Player logged in",
            "A player logged in. Noisy on a busy hotel.",
            "{player} logged in",
            "",
            GREY,
            ["player"]
        ),
        new(
            PLAYER_LOGOUT,
            "Player logged out",
            "A player's connection went. Noisy on a busy hotel.",
            "{player} logged out",
            "",
            GREY,
            ["player"]
        ),
        new(
            SERVER_STARTED,
            "Server started",
            "The server started (not a reload of this plugin).",
            "The hotel is up",
            "",
            GREEN,
            []
        ),
        new(
            SERVER_STOPPING,
            "Server stopping",
            "The server is stopping.",
            "The hotel is going down",
            "",
            GREY,
            []
        ),
        new(
            HOTEL_COUNTDOWN,
            "Restart, shutdown or maintenance",
            "A countdown to a restart, a shutdown or maintenance started.",
            "{kind} in {minutes} minutes",
            "{reason}",
            ORANGE,
            ["kind", "minutes", "reason", "at"]
        ),
        new(
            HOTEL_COUNTDOWN_CANCELLED,
            "Countdown called off",
            "A countdown to a restart, a shutdown or maintenance was called off.",
            "{kind} called off",
            "",
            GREY,
            ["kind"]
        ),
        new(
            HOTEL_MAINTENANCE,
            "Maintenance began",
            "The hotel went into maintenance.",
            "The hotel is in maintenance",
            "{reason}",
            ORANGE,
            ["reason"]
        ),
    ];

    private static readonly Dictionary<string, EventInfo> BY_KEY = ALL.ToDictionary(x => x.Key);

    /// <summary>The routing key of a plugin's notice topic.</summary>
    public static string PluginKey(string source, string topic) =>
        $"{PLUGIN_PREFIX}{source}:{topic}";

    /// <summary>What an event key is: one of <see cref="ALL"/>, or a plugin notice topic's.</summary>
    public static EventInfo? Find(string key) =>
        BY_KEY.TryGetValue(key, out var info) ? info
        : key.StartsWith(PLUGIN_PREFIX) ? PluginNotice(key)
        : null;

    private static EventInfo PluginNotice(string key) =>
        new(
            key,
            $"Plugin {key[PLUGIN_PREFIX.Length..].Replace(':', ' ')}",
            "A notice another plugin raised.",
            "{title}",
            "{text}",
            BLUE,
            ["title", "text", "source", "topic"]
        );
}
