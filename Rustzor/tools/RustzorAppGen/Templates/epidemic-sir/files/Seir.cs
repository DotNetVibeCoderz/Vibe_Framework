namespace {{Namespace}};

public sealed record Scenario
{
    public double Population { get; init; } = 1_000_000;
    public double R0 { get; init; } = 2.8;
    public double IncubationDays { get; init; } = 5.2;
    public double InfectiousDays { get; init; } = 7;
    public double VaccinatedShare { get; init; } = 0;
    public int LockdownStart { get; init; } = 40;
    public int LockdownDays { get; init; } = 0;
    /// <summary>How much a lockdown cuts transmission, 0–1.</summary>
    public double LockdownEffect { get; init; } = 0.6;
    public double HospitalRate { get; init; } = 0.04;
    public double HospitalBeds { get; init; } = 3_000;
    public int Days { get; init; } = 240;
}

public sealed record Day(int T, double S, double E, double I, double R, double Hospital);

public sealed record Outcome(List<Day> Days, double PeakInfected, int PeakDay, double TotalInfected, int DaysOverCapacity, double HerdThreshold);

/// <summary>
/// SEIR: Susceptible → Exposed → Infectious → Removed.
/// dS/dt = −βSI/N, dE/dt = βSI/N − σE, dI/dt = σE − γI, dR/dt = γI,
/// with σ = 1/incubation, γ = 1/infectious period, β = R₀γ (cut during a lockdown).
/// Integrated with fourth-order Runge-Kutta, four steps a day.
/// </summary>
public static class Seir
{
    public static Outcome Run(Scenario s)
    {
        var n = s.Population;
        var sigma = 1 / s.IncubationDays;
        var gamma = 1 / s.InfectiousDays;
        // Start with 10 infectious people; vaccinated people begin in R.
        double S = n * (1 - s.VaccinatedShare) - 10, E = 0, I = 10, R = n * s.VaccinatedShare;
        var days = new List<Day> { new(0, S, E, I, R, I * s.HospitalRate) };
        const int steps = 4;
        const double h = 1.0 / steps;

        for (var t = 1; t <= s.Days; t++)
        {
            var locked = t >= s.LockdownStart && t < s.LockdownStart + s.LockdownDays;
            var beta = s.R0 * gamma * (locked ? 1 - s.LockdownEffect : 1);
            for (var k = 0; k < steps; k++)
            {
                var (dS1, dE1, dI1, dR1) = Rates(S, E, I, beta, sigma, gamma, n);
                var (dS2, dE2, dI2, dR2) = Rates(S + h / 2 * dS1, E + h / 2 * dE1, I + h / 2 * dI1, beta, sigma, gamma, n);
                var (dS3, dE3, dI3, dR3) = Rates(S + h / 2 * dS2, E + h / 2 * dE2, I + h / 2 * dI2, beta, sigma, gamma, n);
                var (dS4, dE4, dI4, dR4) = Rates(S + h * dS3, E + h * dE3, I + h * dI3, beta, sigma, gamma, n);
                S += h / 6 * (dS1 + 2 * dS2 + 2 * dS3 + dS4);
                E += h / 6 * (dE1 + 2 * dE2 + 2 * dE3 + dE4);
                I += h / 6 * (dI1 + 2 * dI2 + 2 * dI3 + dI4);
                R += h / 6 * (dR1 + 2 * dR2 + 2 * dR3 + dR4);
            }
            days.Add(new Day(t, S, E, I, R, I * s.HospitalRate));
        }

        var peak = days.MaxBy(d => d.I)!;
        return new Outcome(
            days,
            peak.I,
            peak.T,
            n - days[^1].S - n * s.VaccinatedShare,
            days.Count(d => d.Hospital > s.HospitalBeds),
            1 - 1 / s.R0);
    }

    private static (double, double, double, double) Rates(double S, double E, double I, double beta, double sigma, double gamma, double n)
    {
        var infection = beta * S * I / n;
        return (-infection, infection - sigma * E, sigma * E - gamma * I, gamma * I);
    }
}
