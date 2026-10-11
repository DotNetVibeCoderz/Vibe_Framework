using System.Globalization;

namespace {{Namespace}};

/// <summary>Descriptive statistics for a sample.</summary>
public sealed class Summary
{
    public int N { get; init; }
    public double Mean { get; init; }
    public double Median { get; init; }
    public double StdDev { get; init; }
    public double Min { get; init; }
    public double Max { get; init; }
    public double Q1 { get; init; }
    public double Q3 { get; init; }
    public double Skewness { get; init; }
    public double Iqr => Q3 - Q1;
    public double LowerFence => Q1 - 1.5 * Iqr;
    public double UpperFence => Q3 + 1.5 * Iqr;
}

public static class Stats
{
    /// <summary>Numbers separated by commas, spaces, semicolons or new lines.</summary>
    public static List<double> ParseNumbers(string text)
    {
        var list = new List<double>();
        foreach (var token in text.Split(new[] { ',', ';', ' ', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries))
            if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) list.Add(v);
        return list;
    }

    /// <summary>One "x,y" (or "x y") pair per line.</summary>
    public static List<(double X, double Y)> ParsePairs(string text)
    {
        var list = new List<(double, double)>();
        foreach (var line in text.Split('\n'))
        {
            var parts = line.Split(new[] { ',', ';', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 &&
                double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x) &&
                double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y))
                list.Add((x, y));
        }
        return list;
    }

    /// <summary>Linear interpolation between closest ranks (the method spreadsheets use).</summary>
    public static double Quantile(IReadOnlyList<double> sorted, double p)
    {
        if (sorted.Count == 0) return double.NaN;
        var pos = (sorted.Count - 1) * p;
        var lo = (int)Math.Floor(pos);
        var hi = (int)Math.Ceiling(pos);
        return sorted[lo] + (sorted[hi] - sorted[lo]) * (pos - lo);
    }

    public static Summary Describe(IReadOnlyList<double> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        var n = sorted.Count;
        if (n == 0) return new Summary();
        var mean = sorted.Average();
        var variance = n > 1 ? sorted.Sum(v => (v - mean) * (v - mean)) / (n - 1) : 0;
        var sd = Math.Sqrt(variance);
        var skew = n > 2 && sd > 0 ? sorted.Sum(v => Math.Pow((v - mean) / sd, 3)) * n / ((n - 1.0) * (n - 2.0)) : 0;
        return new Summary
        {
            N = n,
            Mean = mean,
            Median = Quantile(sorted, 0.5),
            StdDev = sd,
            Min = sorted[0],
            Max = sorted[^1],
            Q1 = Quantile(sorted, 0.25),
            Q3 = Quantile(sorted, 0.75),
            Skewness = skew,
        };
    }

    /// <summary>Counts per bin over [min, max].</summary>
    public static (List<string> Labels, List<double> Counts) Histogram(IReadOnlyList<double> values, int bins)
    {
        var labels = new List<string>();
        var counts = new List<double>();
        if (values.Count == 0 || bins < 1) return (labels, counts);
        var min = values.Min();
        var max = values.Max();
        var width = (max - min) / bins;
        if (width <= 0) width = 1;
        for (var i = 0; i < bins; i++)
        {
            labels.Add((min + i * width).ToString("0.##", CultureInfo.InvariantCulture));
            counts.Add(0);
        }
        foreach (var v in values)
        {
            var index = Math.Min(bins - 1, (int)((v - min) / width));
            counts[index]++;
        }
        return (labels, counts);
    }

    /// <summary>Least-squares line y = a + b·x, with the coefficient of determination.</summary>
    public static (double Intercept, double Slope, double R2, double R) Regression(IReadOnlyList<(double X, double Y)> points)
    {
        var n = points.Count;
        if (n < 2) return (double.NaN, double.NaN, double.NaN, double.NaN);
        var mx = points.Average(p => p.X);
        var my = points.Average(p => p.Y);
        var sxy = points.Sum(p => (p.X - mx) * (p.Y - my));
        var sxx = points.Sum(p => (p.X - mx) * (p.X - mx));
        var syy = points.Sum(p => (p.Y - my) * (p.Y - my));
        var slope = sxx == 0 ? 0 : sxy / sxx;
        var r = sxx == 0 || syy == 0 ? 0 : sxy / Math.Sqrt(sxx * syy);
        return (my - slope * mx, slope, r * r, r);
    }
}
