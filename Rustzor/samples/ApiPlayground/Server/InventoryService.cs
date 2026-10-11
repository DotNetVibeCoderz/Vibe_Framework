using ApiPlayground.Server.Grpc;
using Grpc.Core;

namespace ApiPlayground.Server;

/// <summary>The gRPC service behind gRPC-Web: a shop's stock and some live prices.</summary>
public sealed class InventoryService : Inventory.InventoryBase
{
    private static readonly Item[] Items =
    {
        Make(1, "Kopi Gayo 250 g", "Coffee", 85_000, 42, "arabica", "aceh"),
        Make(2, "Kopi Toraja 250 g", "Coffee", 92_000, 18, "arabica", "sulawesi"),
        Make(3, "Kopi Kintamani 250 g", "Coffee", 88_000, 0, "arabica", "bali"),
        Make(4, "V60 dripper", "Gear", 145_000, 9, "brewing"),
        Make(5, "Gooseneck kettle", "Gear", 420_000, 4, "brewing"),
        Make(6, "Hand grinder", "Gear", 650_000, 6, "grinding"),
        Make(7, "Teh Tarik mix", "Tea", 38_000, 60, "instant"),
        Make(8, "Teh Melati 100 g", "Tea", 24_000, 33, "jasmine", "java"),
    };

    private static Item Make(int id, string name, string category, double price, int stock, params string[] tags)
    {
        var item = new Item { Id = id, Name = name, Category = category, Price = price, Stock = stock };
        item.Tags.AddRange(tags);
        return item;
    }

    public override Task<Item> GetItem(GetItemRequest request, ServerCallContext context) =>
        Items.FirstOrDefault(i => i.Id == request.Id) is { } item
            ? Task.FromResult(item)
            : throw new RpcException(new Status(StatusCode.NotFound, $"No item has id {request.Id}."));

    public override async Task ListItems(ListItemsRequest request, IServerStreamWriter<Item> stream, ServerCallContext context)
    {
        foreach (var item in Items.Where(i => request.Category.Length == 0 || i.Category.Equals(request.Category, StringComparison.OrdinalIgnoreCase)))
        {
            await stream.WriteAsync(item);
            await Task.Delay(120, context.CancellationToken);
        }
    }

    public override async Task WatchPrices(WatchRequest request, IServerStreamWriter<PriceTick> stream, ServerCallContext context)
    {
        var symbols = request.Symbols.Count > 0 ? request.Symbols.ToArray() : new[] { "GAYO", "TORAJA", "KINTAMANI" };
        var prices = symbols.ToDictionary(s => s, s => 60_000.0 + s.Sum(c => c) * 40);
        var count = Math.Clamp(request.Count <= 0 ? 30 : request.Count, 1, 500);
        var rng = new Random();
        for (var n = 1; n <= count && !context.CancellationToken.IsCancellationRequested; n++)
        {
            var symbol = symbols[n % symbols.Length];
            var change = Math.Round((rng.NextDouble() - 0.48) * 900, 0);
            prices[symbol] = Math.Max(10_000, prices[symbol] + change);
            await stream.WriteAsync(new PriceTick { Symbol = symbol, Price = prices[symbol], Change = change, UnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Sequence = n });
            await Task.Delay(180, context.CancellationToken);
        }
    }
}
