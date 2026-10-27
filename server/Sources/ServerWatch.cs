using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Turbo.Primitives.Availability;
using TurboDiscord.Events;

namespace TurboDiscord.Sources;

/// <summary>
/// The server's own events: it started (not a reload of the plugin: the host had not started yet
/// when the plugin did), it is stopping, and the hotel's countdowns (a restart, a shutdown or
/// maintenance scheduled, called off, or maintenance begun), read from <see cref="IHotelAvailability"/>
/// every <see cref="POLL"/>. What it sees when it starts is where it starts from, not news.
/// </summary>
public sealed class ServerWatch(
    HotelInbox inbox,
    IHotelAvailability availability,
    TimeProvider time
) : IDisposable
{
    public static readonly TimeSpan POLL = TimeSpan.FromSeconds(2);

    private readonly List<IDisposable> _registrations = [];
    private HotelAvailabilitySnapshot? _last;
    private ITimer? _timer;

    /// <summary>
    /// Starts watching. <paramref name="stopping"/> runs once the server stops, after its event is
    /// in the inbox: the plugin posts it before the process ends.
    /// </summary>
    public void Start(IHostApplicationLifetime lifetime, Action stopping)
    {
        if (!lifetime.ApplicationStarted.IsCancellationRequested)
            _registrations.Add(
                lifetime.ApplicationStarted.Register(() =>
                    inbox.Add(Server(EventCatalog.SERVER_STARTED))
                )
            );

        _registrations.Add(
            lifetime.ApplicationStopping.Register(() =>
            {
                inbox.Add(Server(EventCatalog.SERVER_STOPPING));
                stopping();
            })
        );

        _last = availability.Current;
        _timer = time.CreateTimer(_ => Poll(), null, POLL, POLL);
    }

    /// <summary>Compares the hotel's availability with the last look; public for tests.</summary>
    public void Poll()
    {
        var now = availability.Current;
        var last = _last;

        _last = now;

        if (last is null || Same(last, now))
            return;

        switch (now.Phase)
        {
            case HotelAvailabilityPhase.MaintenanceScheduled
            or HotelAvailabilityPhase.ShutdownScheduled:
                var minutes = now.AtUtc is { } at
                    ? Math.Max(
                        0,
                        (int)Math.Ceiling((at - time.GetUtcNow().UtcDateTime).TotalMinutes)
                    )
                    : 0;

                inbox.Add(
                    new DiscordEvent
                    {
                        Key = EventCatalog.HOTEL_COUNTDOWN,
                        DedupeKey = $"countdown:{now.Phase}:{now.AtUtc?.Ticks}",
                        OccurredAtUtc = time.GetUtcNow().UtcDateTime,
                        Values = new Dictionary<string, string>
                        {
                            ["kind"] = Kind(now),
                            ["minutes"] = minutes.ToString(CultureInfo.InvariantCulture),
                            ["reason"] = now.Reason,
                            ["at"] =
                                now.AtUtc?.ToString("HH:mm 'UTC'", CultureInfo.InvariantCulture)
                                ?? "",
                        },
                        Fields =
                        [
                            new("Kind", Kind(now)),
                            new("At", now.AtUtc?.ToString("u") ?? ""),
                        ],
                    }
                );
                break;

            case HotelAvailabilityPhase.Open
                when last.Phase
                    is HotelAvailabilityPhase.MaintenanceScheduled
                        or HotelAvailabilityPhase.ShutdownScheduled:
                inbox.Add(
                    new DiscordEvent
                    {
                        Key = EventCatalog.HOTEL_COUNTDOWN_CANCELLED,
                        OccurredAtUtc = time.GetUtcNow().UtcDateTime,
                        Values = new Dictionary<string, string> { ["kind"] = Kind(last) },
                    }
                );
                break;

            case HotelAvailabilityPhase.Maintenance:
                inbox.Add(
                    new DiscordEvent
                    {
                        Key = EventCatalog.HOTEL_MAINTENANCE,
                        OccurredAtUtc = time.GetUtcNow().UtcDateTime,
                        Values = new Dictionary<string, string> { ["reason"] = now.Reason },
                    }
                );
                break;
        }
    }

    private static bool Same(HotelAvailabilitySnapshot a, HotelAvailabilitySnapshot b) =>
        a.Phase == b.Phase && a.AtUtc == b.AtUtc && a.Restart == b.Restart;

    private static string Kind(HotelAvailabilitySnapshot s) =>
        s.Phase == HotelAvailabilityPhase.MaintenanceScheduled ? "Maintenance"
        : s.Restart ? "Restart"
        : "Shutdown";

    private DiscordEvent Server(string key) =>
        new() { Key = key, OccurredAtUtc = time.GetUtcNow().UtcDateTime };

    public void Dispose()
    {
        _timer?.Dispose();
        _timer = null;

        foreach (var registration in _registrations)
            registration.Dispose();

        _registrations.Clear();
    }
}
