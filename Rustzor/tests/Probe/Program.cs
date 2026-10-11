using Rustzor.Interop;
var c = Json.Deserialize<Outer.Customer>("{\"id\":2,\"name\":\"Linus\",\"city\":\"Jakarta\"}");
Console.WriteLine($"{c.Id} {c.Name} {c.City}");
var list = Json.DeserializeList<Outer.Customer>("[{\"id\":1,\"name\":\"Ada\"}]");
Console.WriteLine(list[0].Name);
var t = Json.Deserialize<Top>("{\"id\":3,\"name\":\"Top\"}");
Console.WriteLine($"{t.Id} {t.Name}");
public class Outer
{
    public sealed class Customer
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public string City { get; set; } = "";
    }
}
public sealed class Top { public int Id { get; set; } public string Name { get; set; } = ""; }
