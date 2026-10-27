using Microsoft.EntityFrameworkCore;
using Turbo.Database.Context;
using Turbo.Database.Delegates;

namespace TurboDiscord.Database;

/// <summary>
/// The plugin's tables, under its prefix (<c>discord_</c>): the webhooks, which event goes to
/// which, the posts waiting and sent, and how far the asset movements were read. No foreign keys
/// into core.
/// </summary>
public class DiscordDbContext(
    DbContextOptions<DiscordDbContext> options,
    TablePrefixProvider prefix
) : PluginDbContextBase<DiscordDbContext>(options, prefix)
{
    public DbSet<WebhookEntity> Webhooks => Set<WebhookEntity>();

    public DbSet<RouteEntity> Routes => Set<RouteEntity>();

    public DbSet<DeliveryEntity> Deliveries => Set<DeliveryEntity>();

    public DbSet<CursorEntity> Cursors => Set<CursorEntity>();
}
