using Rustzor.Net.GrpcWeb;

namespace ApiPlayground.Data;

// The messages of Server/Protos/playground.proto, written by hand: a line per
// field, numbered as in the .proto file.

public sealed class GetItemRequest : IProtoMessage
{
    public int Id { get; set; }
    public void WriteTo(ProtoWriter w) => w.Int32(1, Id);
    public void ReadFrom(ProtoReader r) { while (r.Next()) if (r.Field == 1) Id = r.Int32(); else r.Skip(); }
}

public sealed class ListItemsRequest : IProtoMessage
{
    public string Category { get; set; } = "";
    public void WriteTo(ProtoWriter w) => w.String(1, Category);
    public void ReadFrom(ProtoReader r) { while (r.Next()) if (r.Field == 1) Category = r.String(); else r.Skip(); }
}

public sealed class Item : IProtoMessage
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Category { get; set; } = "";
    public double Price { get; set; }
    public int Stock { get; set; }
    public List<string> Tags { get; } = new();

    public void WriteTo(ProtoWriter w)
    {
        w.Int32(1, Id);
        w.String(2, Name);
        w.String(3, Category);
        w.Double(4, Price);
        w.Int32(5, Stock);
        w.Strings(6, Tags);
    }

    public void ReadFrom(ProtoReader r)
    {
        while (r.Next())
            switch (r.Field)
            {
                case 1: Id = r.Int32(); break;
                case 2: Name = r.String(); break;
                case 3: Category = r.String(); break;
                case 4: Price = r.Double(); break;
                case 5: Stock = r.Int32(); break;
                case 6: Tags.Add(r.String()); break;
                default: r.Skip(); break;
            }
    }
}

public sealed class WatchRequest : IProtoMessage
{
    public List<string> Symbols { get; } = new();
    public int Count { get; set; }
    public void WriteTo(ProtoWriter w) { w.Strings(1, Symbols); w.Int32(2, Count); }
    public void ReadFrom(ProtoReader r)
    {
        while (r.Next())
            switch (r.Field) { case 1: Symbols.Add(r.String()); break; case 2: Count = r.Int32(); break; default: r.Skip(); break; }
    }
}

public sealed class PriceTick : IProtoMessage
{
    public string Symbol { get; set; } = "";
    public double Price { get; set; }
    public double Change { get; set; }
    public long UnixMs { get; set; }
    public int Sequence { get; set; }

    public void WriteTo(ProtoWriter w) { w.String(1, Symbol); w.Double(2, Price); w.Double(3, Change); w.Int64(4, UnixMs); w.Int32(5, Sequence); }

    public void ReadFrom(ProtoReader r)
    {
        while (r.Next())
            switch (r.Field)
            {
                case 1: Symbol = r.String(); break;
                case 2: Price = r.Double(); break;
                case 3: Change = r.Double(); break;
                case 4: UnixMs = r.Int64(); break;
                case 5: Sequence = r.Int32(); break;
                default: r.Skip(); break;
            }
    }
}
