using System;
using System.IO;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.DependencyInjection;
using Turbo.Contracts.Plugins;
using Turbo.Database.Delegates;

namespace TurboDiscord.Database;

/// <summary>
/// For <c>dotnet ef migrations add</c> only (see README): the table prefix comes from the
/// manifest, as it does when the plugin loads, so the migrations name the tables the plugin uses.
/// </summary>
public class DiscordDbContextFactory : IDesignTimeDbContextFactory<DiscordDbContext>
{
    public DiscordDbContext CreateDbContext(string[] args)
    {
        var manifest = JsonSerializer.Deserialize<PluginManifest>(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "manifest.json"))
        )!;
        var services = new ServiceCollection();

        services.AddSingleton<TablePrefixProvider>(() => manifest.TablePrefix ?? string.Empty);
        services.AddDbContext<DiscordDbContext>(
            (sp, options) =>
            {
                var prefix = sp.GetRequiredService<TablePrefixProvider>();

                options.UseMySql(
                    new MySqlServerVersion(new Version(8, 0, 0)),
                    mysql =>
                        mysql.MigrationsHistoryTable(
                            $"__EFMigrationsHistory_{prefix().TrimEnd('_')}"
                        )
                );
            }
        );

        return services.BuildServiceProvider().GetRequiredService<DiscordDbContext>();
    }
}
