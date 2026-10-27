using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace TurboDiscord.Events;

/// <summary>
/// Writes an event as a Discord embed: the route's title and text with the event's placeholders
/// filled in, its fields, its link, colour and time, each cut to Discord's limits. Values are
/// escaped, so a player named <c>**x**</c> or <c>@everyone</c> is shown as written and pings no
/// one (posts also allow no mentions).
/// </summary>
public static partial class Embeds
{
    public const int MAX_TITLE = 256;
    public const int MAX_DESCRIPTION = 4096;
    public const int MAX_FIELDS = 25;
    public const int MAX_FIELD_NAME = 256;
    public const int MAX_FIELD_VALUE = 1024;
    public const int MAX_FOOTER = 2048;

    /// <summary>The most characters of all a message's embeds together.</summary>
    public const int MAX_MESSAGE_CHARACTERS = 6000;

    /// <summary>The most embeds in one message.</summary>
    public const int MAX_EMBEDS = 10;

    [GeneratedRegex(@"\{([a-z_]+)\}")]
    private static partial Regex Placeholder();

    /// <summary>
    /// <paramref name="template"/> with each <c>{name}</c> the values have replaced by its value,
    /// escaped for Discord unless <paramref name="escape"/> is false (for the panel); a placeholder
    /// the event does not fill is left as written.
    /// </summary>
    public static string Render(
        string template,
        IReadOnlyDictionary<string, string> values,
        bool escape = true
    ) =>
        Placeholder()
            .Replace(
                template,
                m =>
                    values.TryGetValue(m.Groups[1].Value, out var v)
                        ? (escape ? Escape(v) : v)
                        : m.Value
            );

    /// <summary>The text as written: Discord's markdown and mentions do nothing in it.</summary>
    public static string Escape(string text)
    {
        var builder = new StringBuilder(text.Length + 8);

        foreach (var c in text)
        {
            if (c is '\\' or '*' or '_' or '~' or '`' or '|' or '>' or '[' or ']' or '#')
                builder.Append('\\');

            builder.Append(c);

            // "@everyone" and "@here" stay text even where mentions are parsed.
            if (c == '@')
                builder.Append('​');
        }

        return builder.ToString();
    }

    /// <summary>The embed of <paramref name="evt"/> under its route's title and text.</summary>
    public static JsonObject Build(
        DiscordEvent evt,
        EventInfo info,
        string? title,
        string? text,
        string footer
    )
    {
        var embed = new JsonObject
        {
            ["title"] = Cut(Render(title ?? info.Title, evt.Values), MAX_TITLE),
            ["color"] = evt.Colour ?? info.Colour,
            ["timestamp"] = DateTime.SpecifyKind(evt.OccurredAtUtc, DateTimeKind.Utc).ToString("O"),
        };
        var description = Render(text ?? info.Text, evt.Values).Trim();

        if (description.Length > 0)
            embed["description"] = Cut(description, MAX_DESCRIPTION);

        if (
            Uri.TryCreate(evt.Url, UriKind.Absolute, out var url) && url.Scheme is "http" or "https"
        )
            embed["url"] = url.ToString();

        var fields = evt
            .Fields.Where(x => !string.IsNullOrWhiteSpace(x.Name) && !string.IsNullOrEmpty(x.Value))
            .Take(MAX_FIELDS)
            .Select(x =>
                (JsonNode)
                    new JsonObject
                    {
                        ["name"] = Cut(Escape(x.Name), MAX_FIELD_NAME),
                        ["value"] = Cut(Escape(x.Value), MAX_FIELD_VALUE),
                        ["inline"] = x.Inline,
                    }
            )
            .ToArray();

        if (fields.Length > 0)
            embed["fields"] = new JsonArray(fields);

        if (!string.IsNullOrWhiteSpace(footer))
            embed["footer"] = new JsonObject { ["text"] = Cut(footer, MAX_FOOTER) };

        return embed;
    }

    /// <summary>The characters Discord counts of an embed against <see cref="MAX_MESSAGE_CHARACTERS"/>.</summary>
    public static int Characters(JsonObject embed) =>
        Length(embed["title"])
        + Length(embed["description"])
        + Length(embed["footer"]?["text"])
        + (embed["fields"] as JsonArray ?? []).Sum(x => Length(x?["name"]) + Length(x?["value"]));

    private static int Length(JsonNode? node) => node?.GetValue<string>().Length ?? 0;

    public static string Cut(string text, int max) =>
        text.Length <= max ? text : string.Concat(text.AsSpan(0, max - 1), "…");
}
