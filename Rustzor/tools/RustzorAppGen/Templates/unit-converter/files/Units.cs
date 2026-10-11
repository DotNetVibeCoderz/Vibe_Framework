using System.Globalization;

namespace {{Namespace}};

/// <summary>A unit as a linear map to its category's base unit: base = value × Factor + Offset.</summary>
public sealed record Unit(string Id, string Name, string Symbol, double Factor, double Offset = 0)
{
    public double ToBase(double v) => v * Factor + Offset;
    public double FromBase(double b) => (b - Offset) / Factor;
}

public sealed record Quantity(string Id, string Name, string Icon, string BaseNote, Unit[] Units);

public static class Units
{
    public static readonly Quantity[] All =
    {
        new("length", "Length", "arrow-right", "metre", new Unit[]
        {
            new("mm", "Millimetre", "mm", 0.001), new("cm", "Centimetre", "cm", 0.01), new("m", "Metre", "m", 1), new("km", "Kilometre", "km", 1000),
            new("in", "Inch", "in", 0.0254), new("ft", "Foot", "ft", 0.3048), new("yd", "Yard", "yd", 0.9144), new("mi", "Mile", "mi", 1609.344),
            new("nmi", "Nautical mile", "nmi", 1852),
        }),
        new("mass", "Mass", "box", "kilogram", new Unit[]
        {
            new("mg", "Milligram", "mg", 1e-6), new("g", "Gram", "g", 0.001), new("kg", "Kilogram", "kg", 1), new("t", "Tonne", "t", 1000),
            new("oz", "Ounce", "oz", 0.028349523125), new("lb", "Pound", "lb", 0.45359237), new("st", "Stone", "st", 6.35029318),
        }),
        new("temp", "Temperature", "thermometer", "kelvin", new Unit[]
        {
            new("c", "Celsius", "°C", 1, 273.15), new("f", "Fahrenheit", "°F", 5.0 / 9, 273.15 - 32 * 5.0 / 9), new("k", "Kelvin", "K", 1),
        }),
        new("area", "Area", "layers", "square metre", new Unit[]
        {
            new("cm2", "Square centimetre", "cm²", 1e-4), new("m2", "Square metre", "m²", 1), new("ha", "Hectare", "ha", 1e4), new("km2", "Square kilometre", "km²", 1e6),
            new("ft2", "Square foot", "ft²", 0.09290304), new("ac", "Acre", "ac", 4046.8564224), new("mi2", "Square mile", "mi²", 2_589_988.110336),
        }),
        new("volume", "Volume", "box", "litre", new Unit[]
        {
            new("ml", "Millilitre", "mL", 0.001), new("l", "Litre", "L", 1), new("m3", "Cubic metre", "m³", 1000),
            new("tsp", "Teaspoon (US)", "tsp", 0.00492892159375), new("cup", "Cup (US)", "cup", 0.2365882365), new("floz", "Fluid ounce (US)", "fl oz", 0.0295735295625),
            new("gal", "Gallon (US)", "gal", 3.785411784), new("galuk", "Gallon (UK)", "gal UK", 4.54609),
        }),
        new("speed", "Speed", "gauge", "metre per second", new Unit[]
        {
            new("mps", "Metre per second", "m/s", 1), new("kmh", "Kilometre per hour", "km/h", 1 / 3.6), new("mph", "Mile per hour", "mph", 0.44704),
            new("kn", "Knot", "kn", 1852.0 / 3600), new("mach", "Mach (sea level)", "Ma", 340.29),
        }),
        new("pressure", "Pressure", "gauge", "pascal", new Unit[]
        {
            new("pa", "Pascal", "Pa", 1), new("kpa", "Kilopascal", "kPa", 1000), new("bar", "Bar", "bar", 1e5), new("atm", "Atmosphere", "atm", 101_325),
            new("psi", "Pound per sq inch", "psi", 6894.757293168), new("mmhg", "Millimetre of mercury", "mmHg", 133.322387415),
        }),
        new("energy", "Energy", "zap", "joule", new Unit[]
        {
            new("j", "Joule", "J", 1), new("kj", "Kilojoule", "kJ", 1000), new("cal", "Calorie", "cal", 4.184), new("kcal", "Kilocalorie", "kcal", 4184),
            new("wh", "Watt-hour", "Wh", 3600), new("kwh", "Kilowatt-hour", "kWh", 3.6e6), new("btu", "BTU", "BTU", 1055.05585262),
        }),
        new("data", "Data", "cpu", "byte", new Unit[]
        {
            new("bit", "Bit", "bit", 0.125), new("b", "Byte", "B", 1), new("kb", "Kilobyte", "kB", 1e3), new("kib", "Kibibyte", "KiB", 1024),
            new("mb", "Megabyte", "MB", 1e6), new("mib", "Mebibyte", "MiB", 1_048_576), new("gb", "Gigabyte", "GB", 1e9), new("gib", "Gibibyte", "GiB", 1_073_741_824),
            new("tb", "Terabyte", "TB", 1e12),
        }),
        new("time", "Time", "clock", "second", new Unit[]
        {
            new("ms", "Millisecond", "ms", 0.001), new("s", "Second", "s", 1), new("min", "Minute", "min", 60), new("h", "Hour", "h", 3600),
            new("d", "Day", "d", 86_400), new("wk", "Week", "wk", 604_800), new("yr", "Year (365.25 d)", "yr", 31_557_600),
        }),
    };

    public static double Convert(double value, Unit from, Unit to) => to.FromBase(from.ToBase(value));

    /// <summary>Up to 10 significant figures, without trailing zeros or exponent noise.</summary>
    public static string Format(double v)
    {
        if (double.IsNaN(v) || double.IsInfinity(v)) return "—";
        if (v == 0) return "0";
        var abs = Math.Abs(v);
        if (abs >= 1e15 || abs < 1e-6) return v.ToString("0.#####E+0", CultureInfo.InvariantCulture);
        var digits = Math.Max(0, 9 - (int)Math.Floor(Math.Log10(abs)));
        return Math.Round(v, Math.Min(digits, 12)).ToString("#,##0.############", CultureInfo.InvariantCulture);
    }
}
