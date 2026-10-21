using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace TurboDiscord.Delivery;

/// <summary>What Discord made of a post.</summary>
public enum PostOutcome
{
    /// <summary>Taken.</summary>
    Sent,

    /// <summary>Too many posts (429): try again after <see cref="PostResult.RetryAfter"/>; not a failure.</summary>
    RateLimited,

    /// <summary>Discord is down or slow (5xx, a timeout, no connection): worth trying again later.</summary>
    Retry,

    /// <summary>Refused (4xx): the same post would be refused again.</summary>
    Rejected,

    /// <summary>The webhook is gone or its token is wrong (401, 403, 404).</summary>
    WebhookGone,
}

/// <summary>
/// What a post came to, and what Discord said of its limits: wait <see cref="RetryAfter"/> before
/// the next post to this webhook (a 429, or a bucket it said is empty).
/// </summary>
public sealed record PostResult(
    PostOutcome Outcome,
    string? Error = null,
    TimeSpan? RetryAfter = null,
    int Status = 0
);

/// <summary>Posts messages to webhooks; never throws for what Discord answers or for the network.</summary>
public sealed class DiscordClient : IDisposable
{
    public static readonly TimeSpan TIMEOUT = TimeSpan.FromSeconds(10);

    private readonly HttpClient _http;

    public DiscordClient(HttpMessageHandler? handler = null)
    {
        _http = handler is null
            ? new HttpClient { Timeout = TIMEOUT }
            : new HttpClient(handler, disposeHandler: false) { Timeout = TIMEOUT };
    }

    /// <summary>The message Discord is sent: the embeds, under the hotel's name and picture, mentioning no one.</summary>
    public static string Payload(IEnumerable<JsonObject> embeds, DiscordConfig config)
    {
        var payload = new JsonObject
        {
            ["embeds"] = new JsonArray([.. embeds.Select(x => (JsonNode)x.DeepClone())]),
            ["allowed_mentions"] = new JsonObject { ["parse"] = new JsonArray() },
        };

        if (!string.IsNullOrWhiteSpace(config.Username))
            payload["username"] = config.Username;

        if (
            Uri.TryCreate(config.AvatarUrl, UriKind.Absolute, out var avatar)
            && avatar.Scheme == "https"
        )
            payload["avatar_url"] = avatar.ToString();

        return payload.ToJsonString();
    }

    public async Task<PostResult> PostAsync(string url, string payload, CancellationToken ct)
    {
        try
        {
            using var body = new StringContent(payload, Encoding.UTF8, "application/json");
            using var response = await _http.PostAsync(url, body, ct).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
                return new PostResult(
                    PostOutcome.Sent,
                    RetryAfter: BucketEmpty(response),
                    Status: status
                );

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
                return new PostResult(
                    PostOutcome.RateLimited,
                    "Discord: too many requests",
                    RetryAfter(response, text) ?? TimeSpan.FromSeconds(5),
                    status
                );

            var error = $"Discord answered {status}{Message(text)}";

            return status switch
            {
                401 or 403 or 404 => new PostResult(PostOutcome.WebhookGone, error, Status: status),
                >= 500 => new PostResult(PostOutcome.Retry, error, Status: status),
                _ => new PostResult(PostOutcome.Rejected, error, Status: status),
            };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new PostResult(PostOutcome.Retry, "Discord did not answer in time");
        }
        catch (HttpRequestException ex)
        {
            return new PostResult(PostOutcome.Retry, $"Could not reach the webhook: {ex.Message}");
        }
    }

    /// <summary>A 429's wait: its JSON <c>retry_after</c> (seconds, with a fraction), else the <c>Retry-After</c> header.</summary>
    private static TimeSpan? RetryAfter(HttpResponseMessage response, string body)
    {
        try
        {
            if (
                JsonNode.Parse(body)?["retry_after"] is JsonValue value
                && value.TryGetValue<double>(out var seconds)
            )
                return TimeSpan.FromSeconds(Math.Max(seconds, 0.1));
        }
        catch (JsonException) { }

        if (response.Headers.RetryAfter?.Delta is { } delta)
            return delta;

        return Header(response, "Retry-After") is { } header ? TimeSpan.FromSeconds(header) : null;
    }

    /// <summary>When Discord says the webhook's bucket is empty, how long until it fills again.</summary>
    private static TimeSpan? BucketEmpty(HttpResponseMessage response) =>
        Header(response, "X-RateLimit-Remaining") is 0
        && Header(response, "X-RateLimit-Reset-After") is { } reset
            ? TimeSpan.FromSeconds(reset)
            : null;

    private static double? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values)
        && double.TryParse(
            values.FirstOrDefault(),
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out var value
        )
            ? value
            : null;

    private static string Message(string body)
    {
        try
        {
            if (JsonNode.Parse(body)?["message"]?.GetValue<string>() is { Length: > 0 } message)
                return $": {message}";
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException) { }

        return string.Empty;
    }

    public void Dispose() => _http.Dispose();
}
