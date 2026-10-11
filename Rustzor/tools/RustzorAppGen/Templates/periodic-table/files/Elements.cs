using System.Globalization;
using System.Text;

namespace {{Namespace}};

public sealed record Element(int Number, string Symbol, string Name, double Mass, string Category, int Group, int Period)
{
    private static readonly HashSet<string> Gases = new() { "H", "He", "N", "O", "F", "Ne", "Cl", "Ar", "Kr", "Xe" };

    public string State => Gases.Contains(Symbol) ? "Gas" : Symbol == "Br" ? "Liquid" : "Solid";

    public string CategoryLabel => Category switch
    {
        "alkali" => "Alkali metal", "alkaline" => "Alkaline earth metal", "transition" => "Transition metal",
        "post-transition" => "Post-transition metal", "metalloid" => "Metalloid", "nonmetal" => "Reactive nonmetal",
        "halogen" => "Halogen", "noble" => "Noble gas", _ => Category,
    };

    /// <summary>Ground-state electron configuration by the aufbau order, with the known exceptions.</summary>
    public string Configuration
    {
        get
        {
            if (Exceptions.TryGetValue(Number, out var known)) return known;
            var order = new (string Name, int Capacity)[]
            {
                ("1s", 2), ("2s", 2), ("2p", 6), ("3s", 2), ("3p", 6), ("4s", 2), ("3d", 10), ("4p", 6), ("5s", 2), ("4d", 10), ("5p", 6),
            };
            var left = Number;
            var sb = new StringBuilder();
            foreach (var (name, capacity) in order)
            {
                if (left <= 0) break;
                var n = Math.Min(capacity, left);
                left -= n;
                sb.Append(name).Append(Superscript(n)).Append(' ');
            }
            return sb.ToString().Trim();
        }
    }

    private static readonly Dictionary<int, string> Exceptions = new()
    {
        [24] = "[Ar] 3d⁵ 4s¹", [29] = "[Ar] 3d¹⁰ 4s¹", [41] = "[Kr] 4d⁴ 5s¹", [42] = "[Kr] 4d⁵ 5s¹",
        [44] = "[Kr] 4d⁷ 5s¹", [45] = "[Kr] 4d⁸ 5s¹", [46] = "[Kr] 4d¹⁰", [47] = "[Kr] 4d¹⁰ 5s¹",
    };

    private static string Superscript(int n)
    {
        const string digits = "⁰¹²³⁴⁵⁶⁷⁸⁹";
        return string.Concat(n.ToString(CultureInfo.InvariantCulture).Select(c => digits[c - '0']));
    }
}

public static class PeriodicTable
{
    // number, symbol, name, mass, category, group, period
    private const string Data = """
        1,H,Hydrogen,1.008,nonmetal,1,1
        2,He,Helium,4.0026,noble,18,1
        3,Li,Lithium,6.94,alkali,1,2
        4,Be,Beryllium,9.0122,alkaline,2,2
        5,B,Boron,10.81,metalloid,13,2
        6,C,Carbon,12.011,nonmetal,14,2
        7,N,Nitrogen,14.007,nonmetal,15,2
        8,O,Oxygen,15.999,nonmetal,16,2
        9,F,Fluorine,18.998,halogen,17,2
        10,Ne,Neon,20.180,noble,18,2
        11,Na,Sodium,22.990,alkali,1,3
        12,Mg,Magnesium,24.305,alkaline,2,3
        13,Al,Aluminium,26.982,post-transition,13,3
        14,Si,Silicon,28.085,metalloid,14,3
        15,P,Phosphorus,30.974,nonmetal,15,3
        16,S,Sulfur,32.06,nonmetal,16,3
        17,Cl,Chlorine,35.45,halogen,17,3
        18,Ar,Argon,39.948,noble,18,3
        19,K,Potassium,39.098,alkali,1,4
        20,Ca,Calcium,40.078,alkaline,2,4
        21,Sc,Scandium,44.956,transition,3,4
        22,Ti,Titanium,47.867,transition,4,4
        23,V,Vanadium,50.942,transition,5,4
        24,Cr,Chromium,51.996,transition,6,4
        25,Mn,Manganese,54.938,transition,7,4
        26,Fe,Iron,55.845,transition,8,4
        27,Co,Cobalt,58.933,transition,9,4
        28,Ni,Nickel,58.693,transition,10,4
        29,Cu,Copper,63.546,transition,11,4
        30,Zn,Zinc,65.38,transition,12,4
        31,Ga,Gallium,69.723,post-transition,13,4
        32,Ge,Germanium,72.630,metalloid,14,4
        33,As,Arsenic,74.922,metalloid,15,4
        34,Se,Selenium,78.971,nonmetal,16,4
        35,Br,Bromine,79.904,halogen,17,4
        36,Kr,Krypton,83.798,noble,18,4
        37,Rb,Rubidium,85.468,alkali,1,5
        38,Sr,Strontium,87.62,alkaline,2,5
        39,Y,Yttrium,88.906,transition,3,5
        40,Zr,Zirconium,91.224,transition,4,5
        41,Nb,Niobium,92.906,transition,5,5
        42,Mo,Molybdenum,95.95,transition,6,5
        43,Tc,Technetium,98,transition,7,5
        44,Ru,Ruthenium,101.07,transition,8,5
        45,Rh,Rhodium,102.91,transition,9,5
        46,Pd,Palladium,106.42,transition,10,5
        47,Ag,Silver,107.87,transition,11,5
        48,Cd,Cadmium,112.41,transition,12,5
        49,In,Indium,114.82,post-transition,13,5
        50,Sn,Tin,118.71,post-transition,14,5
        51,Sb,Antimony,121.76,metalloid,15,5
        52,Te,Tellurium,127.60,metalloid,16,5
        53,I,Iodine,126.90,halogen,17,5
        54,Xe,Xenon,131.29,noble,18,5
        """;

    public static readonly List<Element> All = Data.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(line => line.Split(','))
        .Select(p => new Element(int.Parse(p[0]), p[1], p[2], double.Parse(p[3], CultureInfo.InvariantCulture), p[4], int.Parse(p[5]), int.Parse(p[6])))
        .ToList();

    public static readonly string[] Categories = { "alkali", "alkaline", "transition", "post-transition", "metalloid", "nonmetal", "halogen", "noble" };
}
