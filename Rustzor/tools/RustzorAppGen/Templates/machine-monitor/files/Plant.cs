namespace {{Namespace}};

public enum MachineState { Running, Idle, Fault, Maintenance }

public sealed class Machine
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Kind { get; init; }
    public double NominalTemp { get; init; }
    public double NominalRpm { get; init; }
    public double CycleSeconds { get; init; }

    public MachineState State { get; set; } = MachineState.Running;
    public double Temperature { get; set; }
    public double Vibration { get; set; }
    public double Rpm { get; set; }
    public int Good { get; set; }
    public int Scrap { get; set; }
    public double RunSeconds { get; set; }
    public double PlannedSeconds { get; set; }
    public double Wear { get; set; }

    /// <summary>Last two minutes, one sample per tick.</summary>
    public List<double> TempHistory { get; } = new();
    public List<double> VibrationHistory { get; } = new();

    // OEE = availability × performance × quality.
    public double Availability => PlannedSeconds <= 0 ? 1 : RunSeconds / PlannedSeconds;
    public double Performance => RunSeconds <= 0 ? 0 : Math.Min(1, (Good + Scrap) * CycleSeconds / RunSeconds);
    public double Quality => Good + Scrap == 0 ? 1 : (double)Good / (Good + Scrap);
    public double Oee => Availability * Performance * Quality;
}

public sealed class Alarm
{
    public required int Id { get; init; }
    public required string MachineId { get; init; }
    public required string Message { get; init; }
    public required string Severity { get; init; }
    public required DateTime Raised { get; init; }
    public DateTime? Acknowledged { get; set; }
}

public sealed class Limits
{
    public double MaxTemperature { get; set; } = 85;
    public double MaxVibration { get; set; } = 7.1; // mm/s RMS, ISO 10816 zone C for medium machines
}

/// <summary>
/// The line and a simulator for it. Replace <see cref="Tick"/>'s readings with
/// values from your PLC gateway or MQTT bridge (through Fetch or a WebSocket)
/// and the pages keep working.
/// </summary>
public sealed class Plant
{
    public const int HistoryLength = 120;
    public static Plant Instance { get; } = new();

    private readonly Random _rng = new(42);
    private int _nextAlarm = 1;

    public List<Machine> Machines { get; } = new()
    {
        new() { Id = "CNC-01", Name = "5-axis mill", Kind = "CNC", NominalTemp = 58, NominalRpm = 12000, CycleSeconds = 4 },
        new() { Id = "CNC-02", Name = "Lathe", Kind = "CNC", NominalTemp = 52, NominalRpm = 3200, CycleSeconds = 3 },
        new() { Id = "PRS-01", Name = "Hydraulic press", Kind = "Press", NominalTemp = 46, NominalRpm = 0, CycleSeconds = 2 },
        new() { Id = "WLD-01", Name = "Robot welder", Kind = "Robot", NominalTemp = 64, NominalRpm = 0, CycleSeconds = 5 },
        new() { Id = "CNV-01", Name = "Main conveyor", Kind = "Conveyor", NominalTemp = 38, NominalRpm = 1450, CycleSeconds = 1 },
        new() { Id = "PNT-01", Name = "Paint booth", Kind = "Booth", NominalTemp = 70, NominalRpm = 900, CycleSeconds = 6 },
    };

    public List<Alarm> Alarms { get; } = new();
    public Limits Limits { get; } = new();
    public int Ticks { get; private set; }

    public event Action? Changed;

    private Plant()
    {
        foreach (var m in Machines)
        {
            m.Temperature = m.NominalTemp;
            m.Rpm = m.NominalRpm;
            m.Vibration = 2 + _rng.NextDouble();
        }
        // Warm up so the charts open full.
        for (var i = 0; i < HistoryLength; i++) Tick(1, quiet: true);
        Machines[3].Wear = 0.8; // the welder is overdue for service
    }

    public Machine Find(string id) => Machines.First(m => m.Id == id);

    public void Tick(double seconds, bool quiet = false)
    {
        Ticks++;
        foreach (var m in Machines)
        {
            m.PlannedSeconds += seconds;
            if (m.State == MachineState.Running)
            {
                m.RunSeconds += seconds;
                m.Wear = Math.Min(1, m.Wear + 0.0008 * seconds);
                // Heat drifts toward a target that rises with wear.
                var target = m.NominalTemp * (1 + 0.35 * m.Wear * m.Wear);
                m.Temperature += (target - m.Temperature) * 0.08 + Noise(0.6);
                m.Vibration = Math.Max(0.4, 1.8 + 6.5 * m.Wear * m.Wear + Noise(0.35));
                m.Rpm = m.NominalRpm == 0 ? 0 : m.NominalRpm * (1 + Noise(0.004));
                if (_rng.NextDouble() < seconds / m.CycleSeconds)
                {
                    if (_rng.NextDouble() < 0.01 + 0.08 * m.Wear) m.Scrap++; else m.Good++;
                }
            }
            else
            {
                m.Temperature += (24 - m.Temperature) * 0.05 + Noise(0.2);
                m.Vibration = Math.Max(0.1, 0.2 + Noise(0.05));
                m.Rpm = 0;
            }
            Push(m.TempHistory, m.Temperature);
            Push(m.VibrationHistory, m.Vibration);
            if (!quiet) Check(m);
        }
        if (!quiet) Changed?.Invoke();
    }

    private void Check(Machine m)
    {
        if (m.State != MachineState.Running) return;
        if (m.Temperature > Limits.MaxTemperature + 10 || m.Vibration > Limits.MaxVibration * 1.4)
        {
            m.State = MachineState.Fault;
            Raise(m, $"Tripped: {(m.Temperature > Limits.MaxTemperature + 10 ? $"temperature {m.Temperature:0.0} °C" : $"vibration {m.Vibration:0.0} mm/s")}", "critical");
        }
        else if (m.Temperature > Limits.MaxTemperature) Raise(m, $"Temperature high: {m.Temperature:0.0} °C", "warning");
        else if (m.Vibration > Limits.MaxVibration) Raise(m, $"Vibration high: {m.Vibration:0.0} mm/s", "warning");
    }

    private void Raise(Machine m, string message, string severity)
    {
        // One open alarm per machine and severity, so a hot machine does not flood the log.
        if (Alarms.Any(a => a.MachineId == m.Id && a.Severity == severity && a.Acknowledged is null)) return;
        Alarms.Insert(0, new Alarm { Id = _nextAlarm++, MachineId = m.Id, Message = message, Severity = severity, Raised = DateTime.Now });
    }

    public void Acknowledge(Alarm a) => a.Acknowledged ??= DateTime.Now;

    public void SetState(Machine m, MachineState state)
    {
        if (m.State == MachineState.Fault && state == MachineState.Running) m.Wear *= 0.5; // a reset clears some of the cause
        if (state == MachineState.Maintenance) { m.Wear = 0; }
        m.State = state;
        Changed?.Invoke();
    }

    private double Noise(double scale) => (_rng.NextDouble() * 2 - 1) * scale;

    private static void Push(List<double> list, double v)
    {
        list.Add(v);
        if (list.Count > HistoryLength) list.RemoveAt(0);
    }
}
