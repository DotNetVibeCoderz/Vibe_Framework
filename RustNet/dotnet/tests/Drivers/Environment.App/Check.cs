namespace DriversApp;

/// <summary>
/// Minimal assertion helper for the on-device smoke apps: a mismatch prints a
/// FAIL line (which the E2E test rejects) instead of throwing, so one broken
/// driver does not hide the results of the others.
/// </summary>
public static class Check
{
    public static int Passed;
    public static int Failed;

    public static void Equal(string what, long expected, long actual)
    {
        if (expected == actual)
        {
            Passed++;
            return;
        }
        Failed++;
        Console.WriteLine($"FAIL {what}: expected {expected}, got {actual}");
    }

    /// <summary>Compare doubles at a fixed number of thousandths.</summary>
    public static void Near(string what, double expected, double actual, double tolerance)
    {
        double d = expected - actual;
        if (d < 0)
        {
            d = -d;
        }
        if (d <= tolerance)
        {
            Passed++;
            return;
        }
        Failed++;
        Console.WriteLine($"FAIL {what}: expected {(long)(expected * 1000)}/1000, got {(long)(actual * 1000)}/1000");
    }

    public static void True(string what, bool condition)
    {
        if (condition)
        {
            Passed++;
            return;
        }
        Failed++;
        Console.WriteLine($"FAIL {what}");
    }

    /// <summary>Run one driver's smoke test, reporting a crash as a failure rather than ending the app.</summary>
    public static void Run(string driver, Action body)
    {
        int before = Failed;
        try
        {
            body();
        }
        catch (Exception ex)
        {
            Failed++;
            Console.WriteLine($"FAIL {driver}: threw {ex.Message}");
        }
        Console.WriteLine(Failed == before ? $"ok {driver}" : $"bad {driver}");
    }
}
