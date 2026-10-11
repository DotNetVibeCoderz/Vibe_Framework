namespace {{Namespace}};

/// <summary>Keplerian elements at J2000 (JPL approximate elements, inclination ignored).</summary>
public sealed record Planet(string Name, double A, double E, double MeanLongitude, double Perihelion, double PeriodYears, double RadiusKm, string Color, string Fact);

public static class Orbits
{
    public const double AuKm = 149_597_870.7;
    public const double LightSecondsPerAu = 499.005;
    public static readonly DateTime J2000 = new(2000, 1, 1, 12, 0, 0);

    public static readonly Planet[] Planets =
    {
        new("Mercury", 0.38710, 0.20563, 252.251, 77.457, 0.24085, 2_440, "#a8a29e", "Its orbit is the most eccentric of the eight; perihelion precession helped confirm general relativity."),
        new("Venus", 0.72333, 0.00677, 181.980, 131.602, 0.61520, 6_052, "#e7c27d", "The hottest planet, at 465 °C, from a runaway greenhouse effect."),
        new("Earth", 1.00000, 0.01671, 100.464, 102.937, 1.00002, 6_371, "#3b82f6", "Closest to the Sun in early January, not in northern summer."),
        new("Mars", 1.52368, 0.09340, 355.453, 336.060, 1.88085, 3_390, "#dc6b3a", "Opposition, when Mars is closest and brightest, comes every 26 months."),
        new("Jupiter", 5.20260, 0.04849, 34.404, 14.331, 11.8620, 69_911, "#d4a373", "More than twice the mass of all the other planets together."),
        new("Saturn", 9.55491, 0.05551, 49.944, 93.057, 29.4571, 58_232, "#e9d8a6", "Less dense than water; its rings are mostly ice."),
        new("Uranus", 19.21845, 0.04630, 313.232, 173.005, 84.0205, 25_362, "#8fd3d6", "Tilted 98°, so each pole gets 42 years of sunlight, then 42 of night."),
        new("Neptune", 30.11039, 0.00899, 304.880, 48.124, 164.770, 24_622, "#4f6bd8", "Found in 1846 by mathematics, from wobbles in the orbit of Uranus."),
    };

    /// <summary>Heliocentric position in AU at a time, from the mean anomaly through Kepler's equation.</summary>
    public static (double X, double Y) Position(Planet p, DateTime when)
    {
        var years = (when - J2000).TotalDays / 365.25;
        var meanAnomaly = Rad(p.MeanLongitude - p.Perihelion + 360 * years / p.PeriodYears);
        var e = p.E;
        // Solve E − e·sin E = M by Newton's method.
        var ecc = meanAnomaly;
        for (var i = 0; i < 8; i++) ecc -= (ecc - e * Math.Sin(ecc) - meanAnomaly) / (1 - e * Math.Cos(ecc));
        var trueAnomaly = 2 * Math.Atan2(Math.Sqrt(1 + e) * Math.Sin(ecc / 2), Math.Sqrt(1 - e) * Math.Cos(ecc / 2));
        var r = p.A * (1 - e * Math.Cos(ecc));
        var angle = trueAnomaly + Rad(p.Perihelion);
        return (r * Math.Cos(angle), r * Math.Sin(angle));
    }

    /// <summary>Orbital speed from the vis-viva equation, km/s.</summary>
    public static double SpeedKmS(Planet p, double rAu) => 29.7847 * Math.Sqrt(2 / rAu - 1 / p.A);

    public static double Rad(double degrees) => degrees * Math.PI / 180;
}
