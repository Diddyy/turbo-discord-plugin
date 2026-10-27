using System;
using System.Collections.Generic;

namespace TurboDiscord.Delivery;

/// <summary>
/// Each webhook's pace: at most <see cref="DiscordConfig.MaxPerMinute"/> messages in any minute,
/// and none while Discord said to wait (a 429's retry-after, or a bucket it said is empty). Kept
/// in memory: after a restart Discord's next answer says again where a webhook stands.
/// </summary>
public sealed class WebhookLimiter
{
    private static readonly TimeSpan MINUTE = TimeSpan.FromMinutes(1);

    private readonly Dictionary<int, State> _states = [];
    private readonly object _lock = new();

    /// <summary>When the webhook may next be posted to; now or before when it may at once.</summary>
    public DateTime NextAt(int webhook, DateTime now, int maxPerMinute)
    {
        lock (_lock)
        {
            if (!_states.TryGetValue(webhook, out var state))
                return now;

            state.Sent.RemoveAll(x => x <= now - MINUTE);

            var next = state.BlockedUntil;

            if (maxPerMinute > 0 && state.Sent.Count >= maxPerMinute)
                next = Max(next, state.Sent[state.Sent.Count - maxPerMinute] + MINUTE);

            return next;
        }
    }

    /// <summary>Takes a post's place in the minute when the webhook may be posted to now; false when it must wait.</summary>
    public bool TryTake(int webhook, DateTime now, int maxPerMinute)
    {
        lock (_lock)
        {
            if (NextAt(webhook, now, maxPerMinute) > now)
                return false;

            Of(webhook).Sent.Add(now);

            return true;
        }
    }

    /// <summary>Discord said to wait <paramref name="wait"/> before the next post.</summary>
    public void Block(int webhook, DateTime now, TimeSpan wait)
    {
        lock (_lock)
        {
            var state = Of(webhook);

            state.BlockedUntil = Max(state.BlockedUntil, now + wait);
        }
    }

    public void Forget(int webhook)
    {
        lock (_lock)
            _states.Remove(webhook);
    }

    private State Of(int webhook)
    {
        if (!_states.TryGetValue(webhook, out var state))
            _states[webhook] = state = new State();

        return state;
    }

    private static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;

    private sealed class State
    {
        public DateTime BlockedUntil { get; set; } = DateTime.MinValue;

        public List<DateTime> Sent { get; } = [];
    }
}
