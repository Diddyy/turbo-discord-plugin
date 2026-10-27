using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Turbo.Primitives.Movements;
using TurboDiscord.Events;
using TurboDiscord.Sources;
using Xunit;
using static TurboDiscord.Tests.TestHost;

namespace TurboDiscord.Tests;

/// <summary>Events read from the asset movement outbox, as core writes its rows (docs/movements.md).</summary>
public sealed class MovementTests
{
    private const string LTD_EXTRA =
        """{"STUFF":{"UniqueNumber":7,"UniqueSeries":50,"Data":"0"}}""";

    private static AssetMovement[] Ltd(TestHost host, string op) =>
        [
            host.Move(
                MovementCauses.CATALOG_LTD_RAFFLE,
                op,
                0,
                Credits,
                Wallet(1),
                Nowhere,
                amount: 25,
                causeRef: "9"
            ),
            host.Move(
                MovementCauses.CATALOG_LTD_RAFFLE,
                op,
                1,
                Furni(5000, 100),
                Nowhere,
                Inventory(1),
                causeRef: "9",
                snapshot: new AssetSnapshot(100, LTD_EXTRA)
            ),
        ];

    [Fact]
    public async Task An_ltd_is_read_with_its_serial_series_buyer_and_price()
    {
        using var host = new TestHost();

        var events = await MovementEvents.ReadAsync(
            host.Batch(Ltd(host, "op-ltd")).Movements,
            host.Names,
            host.Now.AddMinutes(-15),
            TestHost.CT
        );
        var evt = Assert.Single(events);

        Assert.Equal(EventCatalog.LTD_PURCHASE, evt.Key);
        Assert.Equal("ltd:op-ltd", evt.DedupeKey);
        Assert.Equal("Alice", evt.Values["player"]);
        Assert.Equal("rare_dragonlamp*1", evt.Values["furni"]);
        Assert.Equal("7", evt.Values["serial"]);
        Assert.Equal("50", evt.Values["series"]);
        Assert.Equal("25 credits", evt.Values["price"]);
        Assert.Equal("https://panel.example/players/1", evt.Url);
    }

    [Fact]
    public async Task Purchases_trades_sales_and_staff_gifts_are_read_and_refunds_and_old_rows_are_not()
    {
        using var host = new TestHost();
        var old = host.Now.AddMinutes(-30);

        var rows = host.Batch(
            // A purchase of two thrones for 1,500 credits and 20 diamonds.
            host.Move(
                MovementCauses.CATALOG_PURCHASE,
                "op-buy",
                0,
                Credits,
                Wallet(1),
                Nowhere,
                amount: 1500
            ),
            host.Move(
                MovementCauses.CATALOG_PURCHASE,
                "op-buy",
                1,
                Diamonds,
                Wallet(1),
                Nowhere,
                amount: 20
            ),
            host.Move(
                MovementCauses.CATALOG_PURCHASE,
                "op-buy",
                2,
                Furni(1, 200),
                Nowhere,
                Inventory(1)
            ),
            host.Move(
                MovementCauses.CATALOG_PURCHASE,
                "op-buy",
                3,
                Furni(2, 200),
                Nowhere,
                Inventory(1)
            ),
            // Its refund is an operation of its own: not an event.
            host.Move(
                MovementCauses.CATALOG_PURCHASE,
                "op-buy-refund",
                0,
                Credits,
                Nowhere,
                Wallet(1),
                amount: 1500
            ),
            // A trade: Alice gives a throne and a sofa, Bob a throne.
            host.Move(
                MovementCauses.TRADE,
                "op-trade",
                0,
                Furni(3, 200),
                Inventory(1),
                Inventory(2),
                actor: 2
            ),
            host.Move(
                MovementCauses.TRADE,
                "op-trade",
                1,
                Furni(4, 300),
                Inventory(1),
                Inventory(2),
                actor: 2
            ),
            host.Move(
                MovementCauses.TRADE,
                "op-trade",
                2,
                Furni(5, 200),
                Inventory(2),
                Inventory(1),
                actor: 2
            ),
            // A marketplace sale: Bob buys Alice's sofa for 40 credits.
            host.Move(
                MovementCauses.MARKET_BUY,
                "op-market",
                0,
                Credits,
                Wallet(2),
                Nowhere,
                amount: 40,
                actor: 2,
                causeRef: "77"
            ),
            host.Move(
                MovementCauses.MARKET_BUY,
                "op-market",
                1,
                Furni(6, 300),
                Offer(1, 77),
                Nowhere,
                actor: 2,
                causeRef: "77"
            ),
            host.Move(
                MovementCauses.MARKET_BUY,
                "op-market",
                2,
                Credits,
                Nowhere,
                Offer(1, 77),
                amount: 40,
                actor: 2,
                causeRef: "77"
            ),
            host.Move(
                MovementCauses.MARKET_FEE,
                "op-market",
                3,
                Credits,
                Offer(1, 77),
                Nowhere,
                amount: 2,
                actor: 2,
                causeRef: "77"
            ),
            host.Move(
                MovementCauses.MARKET_BUY,
                "op-market",
                4,
                Furni(7, 300),
                Nowhere,
                Inventory(2),
                actor: 2,
                causeRef: "77"
            ),
            // :give 500 credits to Bob by Staffy.
            host.Move(
                MovementCauses.STAFF_GIVE_CURRENCY,
                "op-give",
                0,
                Credits,
                Nowhere,
                Wallet(2),
                amount: 500,
                actor: 3
            ),
            // A purchase half an hour ago (the plugin was off): not news.
            host.Move(
                MovementCauses.CATALOG_PURCHASE,
                "op-old",
                0,
                Credits,
                Wallet(1),
                Nowhere,
                amount: 99999,
                at: old
            )
        );

        var events = await MovementEvents.ReadAsync(
            rows.Movements,
            host.Names,
            host.Now.AddMinutes(-15),
            TestHost.CT
        );

        Assert.Equal(
            [
                EventCatalog.CATALOG_PURCHASE,
                EventCatalog.TRADE,
                EventCatalog.MARKET_BUY,
                EventCatalog.STAFF_GIVE_CURRENCY,
            ],
            events.Select(x => x.Key)
        );

        var purchase = events[0];
        Assert.Equal("1,500 credits + 20 diamonds", purchase.Values["price"]);
        Assert.Equal("2 × throne", purchase.Values["furni"]);
        Assert.Equal(1500, purchase.Worth["credits"]);

        var trade = events[1];
        Assert.Equal(("Alice", "Bob"), (trade.Values["player"], trade.Values["partner"]));
        Assert.Equal("throne, sofa", trade.Values["given"]);
        Assert.Equal("throne", trade.Values["received"]);
        Assert.Equal(3, trade.Worth[DiscordEvent.ITEMS]);

        var sale = events[2];
        Assert.Equal(
            ("Bob", "Alice", "sofa", "40 credits"),
            (
                sale.Values["player"],
                sale.Values["seller"],
                sale.Values["furni"],
                sale.Values["price"]
            )
        );

        var give = events[3];
        Assert.Equal(
            ("Staffy", "gave", "Bob", "500 credits"),
            (
                give.Values["staff"],
                give.Values["gave"],
                give.Values["player"],
                give.Values["amount"]
            )
        );
    }

    [Fact]
    public async Task A_batch_delivered_again_or_after_a_restart_posts_nothing_twice_and_keeps_the_highest_cursor()
    {
        using var host = new TestHost();

        host.Route("staff", EventCatalog.LTD_PURCHASE);

        var batch = host.Batch(Ltd(host, "op-ltd"));

        await host.Movements.HandleAsync(batch, TestHost.CT);
        // At least once: the same rows again (a second dispatcher, a retried poll).
        await host.Movements.HandleAsync(batch, TestHost.CT);
        // A late batch below the cursor leaves it where it was.
        await host.Movements.HandleAsync(batch with { Cursor = 1, IsLate = true }, TestHost.CT);

        var delivery = Assert.Single(host.Deliveries());
        Assert.Equal("ltd:op-ltd", delivery.DedupeKey);
        Assert.Equal(batch.Cursor, await host.Movements.GetCursorAsync(TestHost.CT));

        // A new load of the plugin (a restart) reads from the stored cursor with the same tables.
        var reloaded = new MovementSource(
            host.Scopes,
            host.Router,
            host.Names,
            host.Settings,
            host.Time,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<MovementSource>.Instance
        );

        Assert.Equal(batch.Cursor, await reloaded.GetCursorAsync(TestHost.CT));
        await reloaded.HandleAsync(batch, TestHost.CT);
        Assert.Single(host.Deliveries());

        await host.Sender.SendDueAsync(flush: true, TestHost.CT);

        var embed = Assert.Single(host.Discord.Embeds);
        Assert.Equal("Alice got rare\\_dragonlamp\\*1 #7/50", embed["title"]!.GetValue<string>());
    }

    [Fact]
    public void The_serial_is_found_wherever_the_stuff_data_keeps_it()
    {
        Assert.Equal((7, 50), MovementEvents.Limited(LTD_EXTRA));
        Assert.Equal((1, 10), MovementEvents.Limited("""{"UniqueNumber":1,"UniqueSeries":10}"""));
        Assert.Null(MovementEvents.Limited("""{"STUFF":{"Data":"0"}}"""));
        Assert.Null(MovementEvents.Limited("not json"));
        Assert.Null(MovementEvents.Limited(null));
    }
}
