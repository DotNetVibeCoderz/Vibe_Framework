object a = Src.Hydro, b = Src.Hydro, c = Src.Gas;
Console.WriteLine($"{a.Equals(b)} {a.Equals(c)} {a.Equals(2)} {Src.Hydro.Equals(Src.Hydro)} {Src.Hydro.GetHashCode()} {a.GetHashCode()} {Equals(a, b)} {EqualityComparer<Src>.Default.Equals(Src.Hydro, Src.Hydro)} {Src.Hydro.CompareTo(Src.Gas)}");
var set = new HashSet<object> { Src.Hydro, Src.Gas, Src.Hydro };
Console.WriteLine($"set {set.Count}");
enum Src { Coal, Gas, Hydro }
