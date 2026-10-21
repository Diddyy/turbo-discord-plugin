using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orleans;
using Turbo.Contracts.Plugins;
using Turbo.Contracts.Plugins.Admin;
using Turbo.Contracts.Plugins.Exports;
using Turbo.Database.Extensions;
using Turbo.Primitives.Availability;
using Turbo.Primitives.Furniture.Providers;
using Turbo.Primitives.Moderation;
using Turbo.Primitives.Movements;
using TurboDiscord.Database;
using TurboDiscord.Delivery;
using TurboDiscord.Sources;

namespace TurboDiscord;

/// <summary>
/// Posts what a hotel owner wants to hear of to Discord channels through webhooks: LTDs, big
/// purchases, trades and marketplace sales, staff gifts (from the asset movements), new reports,
/// bans, logins, the server starting and stopping, countdowns, and other plugins' notices
/// (<c>PluginNoticeEvent</c>). Each event is off until the hotel routes it to a webhook on the
/// admin panel's Discord pages. Posts wait in <c>discord_deliveries</c> and go out batched and
/// paced per webhook.
/// </summary>
public sealed class DiscordPlugin : ITurboPlugin
{
    private readonly Lock _consumerLock = new();
    private IAssetMovementConsumerRegistry? _consumers;
    private IDisposable? _consumer;
    private RouteSwitches? _switches;
    private MovementSource? _movements;
    private DiscordSender? _sender;
    private HotelEvents? _hotel;
    private ServerWatch? _watch;
    private ILogger? _logger;

    public string Key => "discord";

    public string Version => "1.0.0";

    public void ConfigureServices(IServiceCollection services, PluginManifest manifest)
    {
        services.AddPluginDatabaseContext<DiscordDbContext, DiscordDbModule>();
        // Bound as a host module binds its options: the panel lists them, and saves reach the monitor.
        services.AddOptions<DiscordConfig>().BindConfiguration(DiscordConfig.SECTION_NAME);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(Host<IGrainFactory>);
        services.AddSingleton(Host<IFurnitureDefinitionProvider>);
        services.AddSingleton(Host<IModerationIssueService>);
        services.AddSingleton(Host<ISanctionService>);
        services.AddSingleton(Host<IHotelAvailability>);
        services.AddSingleton<INames, HostNames>();
        services.AddSingleton(_ => new DiscordClient());
        services.AddSingleton<WebhookLimiter>();
        services.AddSingleton<Router>();
        services.AddSingleton<RouteSwitches>();
        services.AddSingleton<HotelInbox>();
        services.AddSingleton<HotelEvents>();
        services.AddSingleton<DiscordSender>();
        services.AddSingleton<MovementSource>();
        services.AddSingleton<ServerWatch>();
        services.AddSingleton<DiscordAdmin>();
        services.AddSingleton<IPluginAdmin>(sp => sp.GetRequiredService<DiscordAdmin>());
    }

    private static T Host<T>(IServiceProvider sp)
        where T : notnull => sp.GetRequiredService<IHostServices>().GetRequiredService<T>();

    public async Task StartAsync(IServiceProvider services, CancellationToken ct)
    {
        _logger = services.GetRequiredService<ILogger<DiscordPlugin>>();
        await services
            .GetRequiredService<DiscordAdmin>()
            .EnsureRoutesAsync(ct)
            .ConfigureAwait(false);

        _switches = services.GetRequiredService<RouteSwitches>();
        await _switches.RefreshAsync(ct).ConfigureAwait(false);

        _sender = services.GetRequiredService<DiscordSender>();
        services.GetRequiredService<Router>().Queued += _sender.Wake;
        _sender.Start();

        _hotel = services.GetRequiredService<HotelEvents>();
        _hotel.Start();

        // The outbox is read only while an event made from it is on.
        _consumers = services
            .GetRequiredService<IHostServices>()
            .GetRequiredService<IAssetMovementConsumerRegistry>();
        _movements = services.GetRequiredService<MovementSource>();
        _switches.Changed += ReadMovementsIfWanted;
        ReadMovementsIfWanted();

        var hotel = _hotel;
        var sender = _sender;

        _watch = services.GetRequiredService<ServerWatch>();
        _watch.Start(
            Host<IHostApplicationLifetime>(services),
            // The process is going: post what is waiting now rather than in a batch.
            () =>
                Task.Run(async () =>
                    {
                        using var flush = new CancellationTokenSource(TimeSpan.FromSeconds(5));

                        await hotel.DrainAsync(flush.Token).ConfigureAwait(false);
                        await sender.SendDueAsync(flush: true, flush.Token).ConfigureAwait(false);
                    })
                    .Wait(TimeSpan.FromSeconds(6))
        );
    }

    private void ReadMovementsIfWanted()
    {
        lock (_consumerLock)
        {
            var wanted = _switches?.AnyFromMovements == true;

            if (wanted && _consumer is null && _consumers is not null && _movements is not null)
            {
                _consumer = _consumers.Register(_movements);
                _logger?.LogInformation("Discord reads the asset movements");
            }
            else if (!wanted && _consumer is not null)
            {
                _consumer.Dispose();
                _consumer = null;
                _logger?.LogInformation(
                    "Discord stopped reading the asset movements: no event from them is on"
                );
            }
        }
    }

    public async Task StopAsync(CancellationToken ct)
    {
        _watch?.Dispose();
        _watch = null;

        if (_switches is not null)
            _switches.Changed -= ReadMovementsIfWanted;

        lock (_consumerLock)
        {
            _consumer?.Dispose();
            _consumer = null;
        }

        if (_hotel is not null)
            await _hotel.StopAsync().ConfigureAwait(false);

        if (_sender is not null)
            await _sender.StopAsync().ConfigureAwait(false);

        _hotel = null;
        _sender = null;
        _movements = null;
        _consumers = null;
    }

    public Task BindExportsAsync(IExportBinder binder, IServiceProvider services) =>
        Task.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
