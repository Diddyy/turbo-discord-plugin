using System;
using System.Threading;
using System.Threading.Tasks;
using Turbo.Contracts.Plugins;
using Turbo.Database.Migrations;

namespace TurboDiscord.Database;

public class DiscordDbModule : IPluginDbModule
{
    public Task MigrateAsync(IServiceProvider sp, CancellationToken ct) =>
        MigrationHelper.MigrateAsync<DiscordDbContext>(sp, ct);

    public Task UninstallAsync(IServiceProvider sp, CancellationToken ct) =>
        MigrationHelper.UninstallAsync<DiscordDbContext>(sp, ct);
}
