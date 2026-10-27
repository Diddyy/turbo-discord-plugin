using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Turbo.Primitives.Movements;
using Turbo.Primitives.Movements.Enums;
using TurboDiscord.Events;

namespace TurboDiscord.Sources;

/// <summary>
/// Reads what a hotel owner wants to hear of out of asset movements (<c>docs/movements.md</c>):
/// one event per operation (a purchase, a trade, a sale), keyed by its operation id so it is
/// posted once however often the movements are delivered. A refund (an operation of its own) and a
/// trade's compensation are not events.
/// </summary>
public static class MovementEvents
{
    private const string REFUND = "-refund";

    /// <summary>The events in <paramref name="rows"/>, in outbox order; markers and old rows are skipped.</summary>
    public static async Task<List<DiscordEvent>> ReadAsync(
        IEnumerable<OutboxMovement> rows,
        INames names,
        DateTime notBeforeUtc,
        CancellationToken ct
    )
    {
        var operations = rows.Select(x => x.Movement)
            .Where(x => x.OccurredAtUtc >= notBeforeUtc && !x.OperationId.EndsWith(REFUND))
            .GroupBy(x => x.OperationId)
            .Select(x => x.OrderBy(m => m.Seq).ToList())
            .ToList();
        var players = await names
            .PlayersAsync(
                operations
                    .SelectMany(x => x)
                    .SelectMany(x =>
                        new[] { x.ActorPlayerId, x.From.OwnerPlayerId, x.To.OwnerPlayerId }
                    ),
                ct
            )
            .ConfigureAwait(false);
        var events = new List<DiscordEvent>();

        foreach (var operation in operations)
        {
            var read = new Operation(operation, players, names);
            var evt = read.Cause switch
            {
                MovementCauses.CATALOG_LTD_RAFFLE => read.Ltd(),
                MovementCauses.CATALOG_PURCHASE => read.Purchase(),
                MovementCauses.TRADE => read.Trade(),
                MovementCauses.MARKET_BUY => read.MarketBuy(),
                MovementCauses.STAFF_GIVE_CURRENCY => read.GiveCurrency(),
                MovementCauses.STAFF_GIVE_ITEM or MovementCauses.STAFF_GIFT => read.GiveItem(),
                MovementCauses.REWARD_TRACK => read.RewardTrack(),
                _ => null,
            };

            if (evt is not null)
                events.Add(evt);
        }

        return events;
    }

    /// <summary>A limited edition's serial and series size, from the stuff data it was made with.</summary>
    public static (int Serial, int Series)? Limited(string? extraData)
    {
        if (string.IsNullOrEmpty(extraData))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(extraData);

            return Find(doc.RootElement);
        }
        catch (JsonException)
        {
            return null;
        }

        static (int, int)? Find(JsonElement e)
        {
            if (e.ValueKind != JsonValueKind.Object)
                return null;

            if (
                e.TryGetProperty("UniqueNumber", out var number)
                && e.TryGetProperty("UniqueSeries", out var series)
                && number.TryGetInt32(out var n)
                && series.TryGetInt32(out var s)
                && n > 0
            )
                return (n, s);

            foreach (var property in e.EnumerateObject())
                if (Find(property.Value) is { } found)
                    return found;

            return null;
        }
    }

    private sealed class Operation(
        List<AssetMovement> rows,
        IReadOnlyDictionary<int, string> players,
        INames names
    )
    {
        private readonly AssetMovement _first = rows[0];

        public string Cause => _first.Cause;

        private string Player(int id) => players.TryGetValue(id, out var name) ? name : $"#{id}";

        private IEnumerable<AssetMovement> Debits =>
            rows.Where(x =>
                x.Asset.Kind == AssetKind.Currency
                && x.From.Kind == PlaceKind.Wallet
                && x.To.Kind == PlaceKind.None
            );

        private static bool IsItem(AssetMovement x) => x.Asset.Kind != AssetKind.Currency;

        private static bool IsCreated(AssetMovement x) =>
            IsItem(x) && x.From.Kind == PlaceKind.None && x.To.Kind != PlaceKind.None;

        private static Dictionary<string, long> Sum(IEnumerable<AssetMovement> currency) =>
            currency
                .GroupBy(x => Currencies.Name(x.Asset.Currency!.Value))
                .ToDictionary(x => x.Key, x => x.Sum(m => m.Amount));

        private string Item(AssetMovement x) =>
            x.Asset.Kind switch
            {
                AssetKind.Pet => "a pet",
                AssetKind.Bot => "a bot",
                _ => names.Furni(x.Asset.DefinitionId),
            };

        /// <summary><c>2 × throne, sofa</c>: items by name, most first.</summary>
        private string Items(IEnumerable<AssetMovement> items)
        {
            var list = items
                .GroupBy(Item)
                .OrderByDescending(x => x.Count())
                .Select(x => x.Count() > 1 ? $"{x.Count()} × {x.Key}" : x.Key)
                .ToList();

            return list.Count == 0 ? "nothing" : string.Join(", ", list);
        }

        private DiscordEvent Event(
            string key,
            string dedupe,
            int player,
            Dictionary<string, string> values,
            Dictionary<string, long>? worth = null,
            List<EventField>? fields = null
        ) =>
            new()
            {
                Key = key,
                DedupeKey = $"{dedupe}:{_first.OperationId}",
                OccurredAtUtc = _first.OccurredAtUtc,
                Values = values,
                Worth = worth ?? [],
                Fields = fields ?? [],
                Url = player > 0 ? names.Panel($"players/{player}") : null,
            };

        public DiscordEvent? Ltd()
        {
            var item = rows.FirstOrDefault(x => x.Asset.Kind == AssetKind.Furni && IsCreated(x));

            if (item is null)
                return null;

            var limited = MovementEvents.Limited(item.Snapshot?.ExtraData);
            var player = item.To.OwnerPlayerId;
            var price = Sum(Debits);

            return Event(
                EventCatalog.LTD_PURCHASE,
                "ltd",
                player,
                new()
                {
                    ["player"] = Player(player),
                    ["furni"] = names.Furni(item.Asset.DefinitionId),
                    ["serial"] = limited?.Serial.ToString() ?? "?",
                    ["series"] = limited?.Series.ToString() ?? "?",
                    ["price"] = Currencies.Format(price),
                },
                price,
                [new("Item id", item.Asset.Id.ToString())]
            );
        }

        public DiscordEvent? Purchase()
        {
            var items = rows.Where(IsCreated).ToList();
            var price = Sum(Debits);

            if (items.Count == 0 && price.Count == 0)
                return null;

            var player = _first.ActorPlayerId;

            return Event(
                EventCatalog.CATALOG_PURCHASE,
                "purchase",
                player,
                new()
                {
                    ["player"] = Player(player),
                    ["furni"] = Items(items),
                    ["items"] = items.Count.ToString(),
                    ["price"] = Currencies.Format(price),
                },
                price
            );
        }

        public DiscordEvent? Trade()
        {
            var items = rows.Where(x =>
                    x.CauseRef != MovementCauses.TRADE_REF_COMPENSATION
                    && IsItem(x)
                    && x.From.Kind == PlaceKind.Inventory
                    && x.To.Kind == PlaceKind.Inventory
                )
                .ToList();

            if (items.Count == 0)
                return null;

            var player = items[0].From.OwnerPlayerId;
            var partner = items[0].To.OwnerPlayerId;

            return Event(
                EventCatalog.TRADE,
                "trade",
                player,
                new()
                {
                    ["player"] = Player(player),
                    ["partner"] = Player(partner),
                    ["given"] = Items(items.Where(x => x.From.OwnerPlayerId == player)),
                    ["received"] = Items(items.Where(x => x.From.OwnerPlayerId == partner)),
                    ["items"] = items.Count.ToString(),
                },
                new() { [DiscordEvent.ITEMS] = items.Count }
            );
        }

        public DiscordEvent? MarketBuy()
        {
            var price = rows.FirstOrDefault(x =>
                x.Cause == MovementCauses.MARKET_BUY
                && x.Asset.Kind == AssetKind.Currency
                && x.From.Kind == PlaceKind.Wallet
            );
            var item = rows.FirstOrDefault(x =>
                IsItem(x) && x.From.Kind == PlaceKind.MarketplaceOffer
            );

            if (price is null || item is null)
                return null;

            var buyer = price.From.OwnerPlayerId;
            var seller = item.From.OwnerPlayerId;
            var paid = Sum([price]);

            return Event(
                EventCatalog.MARKET_BUY,
                "market",
                buyer,
                new()
                {
                    ["player"] = Player(buyer),
                    ["seller"] = Player(seller),
                    ["furni"] = names.Furni(item.Asset.DefinitionId),
                    ["price"] = Currencies.Format(paid),
                },
                paid,
                [new("Offer", _first.CauseRef ?? "")]
            );
        }

        public DiscordEvent? GiveCurrency()
        {
            var move = rows.FirstOrDefault(x => x.Asset.Kind == AssetKind.Currency);

            if (move is null)
                return null;

            var gave = move.To.Kind == PlaceKind.Wallet;
            var player = gave ? move.To.OwnerPlayerId : move.From.OwnerPlayerId;
            var currency = Currencies.Name(move.Asset.Currency!.Value);

            return Event(
                EventCatalog.STAFF_GIVE_CURRENCY,
                "give",
                player,
                new()
                {
                    ["staff"] = move.ActorPlayerId > 0 ? Player(move.ActorPlayerId) : "The server",
                    ["gave"] = gave ? "gave" : "took from",
                    ["player"] = Player(player),
                    ["amount"] = Currencies.Format(move.Amount, currency),
                },
                new() { [currency] = move.Amount }
            );
        }

        public DiscordEvent? GiveItem()
        {
            // A staff gift is a present made into the inventory with its content in it: the content is what was given.
            var items = rows.Where(x =>
                    IsCreated(x)
                    && (Cause == MovementCauses.STAFF_GIFT ? x.To.Kind == PlaceKind.Holder : true)
                )
                .ToList();

            if (items.Count == 0)
                return null;

            var player = items[0].To.OwnerPlayerId;

            return Event(
                EventCatalog.STAFF_GIVE_ITEM,
                "staffitem",
                player,
                new()
                {
                    ["staff"] =
                        _first.ActorPlayerId > 0 ? Player(_first.ActorPlayerId) : "The server",
                    ["player"] = Player(player),
                    ["furni"] = Items(items),
                    ["items"] = items.Count.ToString(),
                },
                new() { [DiscordEvent.ITEMS] = items.Count }
            );
        }

        public DiscordEvent? RewardTrack()
        {
            var credits = rows.Where(x =>
                    x.Asset.Kind == AssetKind.Currency && x.To.Kind == PlaceKind.Wallet
                )
                .ToList();
            var items = rows.Where(IsCreated).ToList();

            if (credits.Count == 0 && items.Count == 0)
                return null;

            var player = (credits.FirstOrDefault() ?? items[0]).To.OwnerPlayerId;
            var prize = new List<string>();

            if (credits.Count > 0)
                prize.Add(Currencies.Format(Sum(credits)));

            if (items.Count > 0)
                prize.Add(Items(items));

            return Event(
                EventCatalog.REWARD_TRACK,
                "track",
                player,
                new() { ["player"] = Player(player), ["prize"] = string.Join(" and ", prize) }
            );
        }
    }
}
