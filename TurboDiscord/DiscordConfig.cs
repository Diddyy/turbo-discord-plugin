using System;
using System.Linq;

namespace TurboDiscord;

/// <summary>
/// The plugin's settings, under <c>Turbo:Discord</c> in the host's configuration, and on the admin
/// panel's Settings page while the plugin is loaded. A change applies at once. Webhooks and which
/// event goes where are kept in the plugin's tables and set on its own pages.
/// </summary>
public sealed class DiscordConfig
{
    public const string SECTION_NAME = "Turbo:Discord";

    /// <summary>Whether anything is posted. Off: nothing is queued, and what is queued waits.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// The name posts are made under; empty: the name the webhook was given in Discord.
    /// </summary>
    public string Username { get; init; } = string.Empty;

    /// <summary>A picture (https) posts are made with; empty: the webhook's own.</summary>
    public string AvatarUrl { get; init; } = string.Empty;

    /// <summary>The footer of every post (the hotel's name); empty: none.</summary>
    public string Footer { get; init; } = string.Empty;

    /// <summary>
    /// How many posts wait at most, over every webhook. Past it the oldest waiting is dropped and
    /// counted: what is new matters more than what Discord could not take for a while.
    /// </summary>
    public int QueueCapacity { get; init; } = 500;

    /// <summary>
    /// How long, in milliseconds, a post waits for others to the same webhook, so a burst goes as
    /// one message of up to 10 embeds.
    /// </summary>
    public int BatchWindowMs { get; init; } = 2000;

    /// <summary>
    /// The most messages sent to one webhook in a minute (Discord allows 30 a channel), on top of
    /// what Discord itself says when it answers.
    /// </summary>
    public int MaxPerMinute { get; init; } = 20;

    /// <summary>How many times a post that failed (Discord down, a timeout) is tried in all before it is given up.</summary>
    public int MaxAttempts { get; init; } = 8;

    /// <summary>How long, in days, sent, failed and dropped posts are kept on the Deliveries page.</summary>
    public int DeliveryRetentionDays { get; init; } = 14;

    /// <summary>
    /// An event older than this, in minutes, when the plugin hears of it is not posted: what happened
    /// while the plugin was off or behind is not news.
    /// </summary>
    public int MaxEventAgeMinutes { get; init; } = 15;

    /// <summary>
    /// The hosts a webhook may post to, comma separated. Discord's by default; a host added here
    /// (a relay of your own) is posted to as if it were Discord.
    /// </summary>
    public string AllowedHosts { get; init; } =
        "discord.com,discordapp.com,ptb.discord.com,canary.discord.com";

    internal bool AllowsHost(string host) =>
        AllowedHosts
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(x => string.Equals(x, host, StringComparison.OrdinalIgnoreCase));
}
