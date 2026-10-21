using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Turbo.Contracts.Plugins.Admin;
using Turbo.Database.Delegates;
using Turbo.Primitives.Movements;
using Turbo.Primitives.Movements.Enums;
using Turbo.Primitives.Players.Wallet;
using TurboDiscord.Database;
using TurboDiscord.Delivery;
using TurboDiscord.Events;
using TurboDiscord.Sources;

namespace TurboDiscord.Tests;

internal sealed class ManualTime : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => Now;

    public void Advance(TimeSpan by) => Now += by;
}

internal sealed class Settings(DiscordConfig value) : IOptionsMonitor<DiscordConfig>
{
    public DiscordConfig Value { get; set; } = value;

    public DiscordConfig CurrentValue => Value;

    public DiscordConfig Get(string? name) => Value;

    public IDisposable? OnChange(Action<DiscordConfig, string?> listener) => null;
}

/// <summary>A fake Discord: keeps every post and answers what the test queued (204 by default).</summary>
internal sealed class FakeDiscord : HttpMessageHandler
{
    public List<(string Url, JsonObject Body)> Posts { get; } = [];

    public Queue<Func<HttpResponseMessage>> Answers { get; } = new();

    public IEnumerable<JsonObject> Embeds =>
        Posts.SelectMany(x => x.Body["embeds"]!.AsArray().Select(e => e!.AsObject()));

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken
    )
    {
        Posts.Add(
            (
                request.RequestUri!.ToString(),
                JsonNode
                    .Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!
                    .AsObject()
            )
        );

        return Answers.TryDequeue(out var answer)
            ? answer()
            : new HttpResponseMessage(HttpStatusCode.NoContent);
    }

    public static HttpResponseMessage Answer(HttpStatusCode status, string? json = null)
    {
        var response = new HttpResponseMessage(status);

        if (json is not null)
            response.Content = new StringContent(json);

        return response;
    }
}

internal sealed class FakeNames : INames
{
    public Dictionary<int, string> Players { get; } =
        new()
        {
            [1] = "Alice",
            [2] = "Bob",
            [3] = "Staffy",
        };

    public Dictionary<int, string> Furni { get; } =
        new()
        {
            [100] = "rare_dragonlamp*1",
            [200] = "throne",
            [300] = "sofa",
        };

    public Task<IReadOnlyDictionary<int, string>> PlayersAsync(
        IEnumerable<int> ids,
        CancellationToken ct
    ) =>
        Task.FromResult<IReadOnlyDictionary<int, string>>(
            ids.Where(x => x > 0)
                .Distinct()
                .ToDictionary(x => x, x => Players.GetValueOrDefault(x, $"#{x}"))
        );

    string INames.Furni(int definitionId) =>
        Furni.GetValueOrDefault(definitionId, $"furni #{definitionId}");

    public string? Panel(string path) => $"https://panel.example/{path}";
}

/// <summary>
/// The plugin's services over an in-memory SQLite database (the real SQL: transactions, unique
/// keys), a fake Discord and a clock the test moves.
/// </summary>
internal sealed class TestHost : IDisposable
{
    public static readonly CancellationToken CT = CancellationToken.None;
    public const string HOOK_URL = "https://discord.com/api/webhooks/123456/secret-token";

    private readonly SqliteConnection _connection = new("Data Source=:memory:");

    public TestHost(DiscordConfig? config = null)
    {
        _connection.Open();
        Settings = new Settings(
            config ?? new DiscordConfig { BatchWindowMs = 2000, MaxPerMinute = 20 }
        );

        var services = new ServiceCollection();

        services.AddSingleton<TablePrefixProvider>(() => "discord_");
        services.AddDbContext<DiscordDbContext>(o => o.UseSqlite(_connection));
        Services = services.BuildServiceProvider();

        using (var scope = Services.CreateScope())
            scope.ServiceProvider.GetRequiredService<DiscordDbContext>().Database.EnsureCreated();

        Scopes = Services.GetRequiredService<IServiceScopeFactory>();
        Client = new DiscordClient(Discord);
        Router = new Router(Settings, Time);
        Switches = new RouteSwitches(Scopes);
        Sender = new DiscordSender(
            Scopes,
            Settings,
            Client,
            Limiter,
            Time,
            NullLogger<DiscordSender>.Instance
        );
        Movements = new MovementSource(
            Scopes,
            Router,
            Names,
            Settings,
            Time,
            NullLogger<MovementSource>.Instance
        );
        AdminService = new DiscordAdmin(Scopes, Settings, Client, Limiter, Switches, Inbox, Time);
        AdminService.EnsureRoutesAsync(CT).GetAwaiter().GetResult();
    }

    public ManualTime Time { get; } = new();

    public Settings Settings { get; }

    public ServiceProvider Services { get; }

    public IServiceScopeFactory Scopes { get; }

    public FakeDiscord Discord { get; } = new();

    public FakeNames Names { get; } = new();

    public DiscordClient Client { get; }

    public WebhookLimiter Limiter { get; } = new();

    public Router Router { get; }

    public RouteSwitches Switches { get; }

    public HotelInbox Inbox { get; } = new();

    public DiscordSender Sender { get; }

    public MovementSource Movements { get; }

    public DiscordAdmin AdminService { get; }

    public DateTime Now => Time.Now.UtcDateTime;

    public T Query<T>(Func<DiscordDbContext, T> query)
    {
        using var scope = Services.CreateScope();

        return query(scope.ServiceProvider.GetRequiredService<DiscordDbContext>());
    }

    /// <summary>Adds a webhook and routes <paramref name="keys"/> to it, on.</summary>
    public int Route(string hook, params string[] keys) => Route(hook, null, keys);

    public int Route(string hook, Action<RouteEntity>? change, params string[] keys)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DiscordDbContext>();
        var webhook = db.Webhooks.FirstOrDefault(x => x.Name == hook);

        if (webhook is null)
        {
            webhook = new WebhookEntity
            {
                Name = hook,
                Url = $"{HOOK_URL}-{hook}",
                CreatedAt = Now,
            };
            db.Webhooks.Add(webhook);
            db.SaveChanges();
        }

        foreach (var key in keys)
        {
            var route = db.Routes.FirstOrDefault(x => x.Key == key);

            if (route is null)
                db.Routes.Add(route = new RouteEntity { Key = key, UpdatedAt = Now });

            route.Enabled = true;
            route.WebhookId = webhook.Id;
            change?.Invoke(route);
        }

        db.SaveChanges();
        Switches.RefreshAsync(CT).GetAwaiter().GetResult();

        return webhook.Id;
    }

    public async Task<int> RouteAsync(params DiscordEvent[] events)
    {
        await using var scope = Services.CreateAsyncScope();

        return await Router.RouteAsync(
            scope.ServiceProvider.GetRequiredService<DiscordDbContext>(),
            events,
            CT
        );
    }

    public List<DeliveryEntity> Deliveries() =>
        Query(db => db.Deliveries.AsNoTracking().OrderBy(x => x.Id).ToList());

    public DiscordEvent Event(
        string key,
        string? dedupe = null,
        Dictionary<string, long>? worth = null
    ) =>
        new()
        {
            Key = key,
            DedupeKey = dedupe,
            OccurredAtUtc = Now,
            Values = new Dictionary<string, string>
            {
                ["player"] = "Alice",
                ["furni"] = "throne",
                ["serial"] = "3",
                ["series"] = "50",
                ["price"] = "5 credits",
            },
            Worth = worth ?? [],
        };

    public Admin Admin() => new(AdminService);

    // Movements as core writes them (docs/movements.md).

    private long _outboxId;

    public OutboxMovement Row(AssetMovement movement) => new(++_outboxId, movement, Now);

    public AssetMovement Move(
        string cause,
        string operation,
        short seq,
        AssetRef asset,
        AssetPlace from,
        AssetPlace to,
        long amount = 1,
        int actor = 1,
        string? causeRef = null,
        AssetSnapshot? snapshot = null,
        DateTime? at = null
    ) =>
        new()
        {
            OperationId = operation,
            Seq = seq,
            OccurredAtUtc = at ?? Now,
            Asset = asset,
            From = from,
            To = to,
            Amount = amount,
            ActorPlayerId = actor,
            Cause = cause,
            CauseRef = causeRef,
            Snapshot = snapshot,
        };

    public static AssetRef Credits => new(AssetKind.Currency, 0, 0, CurrencyKind.Credits);

    public static AssetRef Diamonds =>
        new(AssetKind.Currency, 0, 0, CurrencyKind.ActivityPoints(5));

    public static AssetRef Furni(long id, int definition) =>
        new(AssetKind.Furni, id, definition, null);

    public static AssetPlace Wallet(int player) => new(player, PlaceKind.Wallet, 0, null);

    public static AssetPlace Inventory(int player) => new(player, PlaceKind.Inventory, 0, null);

    public static AssetPlace Offer(int seller, long offer) =>
        new(seller, PlaceKind.MarketplaceOffer, offer, null);

    public static AssetPlace Nowhere => AssetPlace.Nowhere;

    public AssetMovementBatch Batch(params AssetMovement[] movements)
    {
        var rows = movements.Select(Row).ToImmutableArray();

        return new AssetMovementBatch { Movements = rows, Cursor = rows.Max(x => x.Id) };
    }

    public void Dispose()
    {
        Services.Dispose();
        Client.Dispose();
        _connection.Dispose();
    }
}

/// <summary>The admin endpoints by "METHOD pattern", called as the host would.</summary>
internal sealed class Admin : IPluginAdminBuilder
{
    public Admin(DiscordAdmin admin) => admin.Configure(this);

    public Dictionary<
        string,
        (string Node, Func<PluginAdminRequest, CancellationToken, Task<PluginAdminResult>> Handler)
    > Map { get; } = [];

    public List<string> Nodes { get; } = [];

    public List<PluginAdminPage> Pages { get; } = [];

    public string Node(string name, string description)
    {
        Nodes.Add($"plugin.discord.{name}");

        return $"plugin.discord.{name}";
    }

    void IPluginAdminBuilder.Map(
        string method,
        string pattern,
        string node,
        Func<PluginAdminRequest, CancellationToken, Task<PluginAdminResult>> handler
    ) => Map[$"{method} {pattern}"] = (node, handler);

    public void Page(PluginAdminPage page) => Pages.Add(page);

    public Task<PluginAdminResult> CallAsync(
        string route,
        long id = 0,
        string query = "",
        string? body = null
    ) =>
        Map[route]
            .Handler(
                new PluginAdminRequest(
                    route.Split(' ')[0],
                    route,
                    new Dictionary<string, string> { ["id"] = id.ToString() },
                    query
                        .Split('&', StringSplitOptions.RemoveEmptyEntries)
                        .Select(x => x.Split('='))
                        .ToDictionary(x => x[0], x => Uri.UnescapeDataString(x[1])),
                    body is null ? null : JsonDocument.Parse(body).RootElement,
                    9
                ),
                TestHost.CT
            );

    public static string Json(PluginAdminResult result) => JsonSerializer.Serialize(result.Value);
}
