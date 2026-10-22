namespace Meet.Data;

/// <summary>The cooperative's bureaus and its standing call rooms.</summary>
public static class Newsroom
{
    public static readonly Bureau[] Bureaus =
    {
        new("mks", "Makassar", "#0b7a75"),
        new("amq", "Ambon", "#3456d1"),
        new("bit", "Bitung", "#7a3fc4"),
        new("kdi", "Kendari", "#b45309"),
        new("tte", "Ternate", "#2f8f3a"),
    };

    public static readonly Desk[] Desks =
    {
        new("rapat-pagi", "Rapat pagi", "Daily", "Every day 07.00 WITA",
            "The whole cooperative plans today's bulletins: each bureau pitches, the editor decides the running order.",
            new RundownItem[]
            {
                new("BANJIR-BITUNG", "Bitung", 150, "Water at the Girian market, evacuation numbers from BPBD"),
                new("HARGA-CAKALANG", "Ambon", 90, "Skipjack price at Pasar Arumbai after two weeks of high waves"),
                new("PELABUHAN-SOEHATTA", "Makassar", 120, "Container dwell time, the new berth schedule"),
                new("NIKEL-POMALAA", "Kendari", 120, "Smelter run-off: what the fishers say, what the company says"),
                new("FERI-TERNATE-SOFIFI", "Ternate", 60, "Ferry timetable change from Monday"),
                new("CUACA", "Makassar", 45, "BMKG wave warning for the Banda Sea"),
            }),
        new("meja-ambon", "Meja Ambon", "Bureau desk", "Open all day",
            "Ambon's reporters and the Makassar desk editor, for edits, fact checks and voice-over takes.",
            new RundownItem[]
            {
                new("HARGA-CAKALANG", "Ambon", 90, "Second source at the fish auction"),
                new("FESTIVAL-TEULUBA", "Ambon", 60, "Pictures from the bay, captions due 14.00"),
                new("SEKOLAH-SAPARUA", "Ambon", 120, "The ferry is the school bus: the feature, cut to 2 minutes"),
            }),
        new("banjir-bitung", "Banjir Bitung", "Special coverage", "Until the water goes down",
            "The flood desk: Bitung reporters in the field, the Makassar studio and the data team.",
            new RundownItem[]
            {
                new("POSKO-GIRIAN", "Bitung", 90, "Live from the shelter at the Girian school"),
                new("CURAH-HUJAN", "Makassar", 60, "Rainfall against the 10-year average"),
                new("JALAN-PUTUS", "Bitung", 60, "Roads cut between Bitung and Manado"),
                new("BANTUAN", "Bitung", 90, "Who needs what: the list for listeners who want to help"),
            }),
    };

    /// <summary>Names the pre-join screen suggests, so a second tab can join with one click.</summary>
    public static readonly (string Name, string Bureau)[] Suggestions =
    {
        ("Nur Halimah", "mks"), ("Yohanis Latumahina", "amq"), ("Grace Rumengan", "bit"),
        ("La Ode Rahman", "kdi"), ("Fatma Sangaji", "tte"), ("Andi Batari", "mks"),
    };

    public static Bureau BureauOf(string id) => Bureaus.FirstOrDefault(b => b.Id == id) ?? Bureaus[0];

    public static Desk? DeskOf(string id) => Desks.FirstOrDefault(d => d.Id == id);

    public static string Initials(string name)
    {
        var parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return "?";
        if (parts.Length == 1) return parts[0].Substring(0, Math.Min(2, parts[0].Length)).ToUpperInvariant();
        return (parts[0].Substring(0, 1) + parts[^1].Substring(0, 1)).ToUpperInvariant();
    }

    /// <summary>Broadcast running time: 2'30.</summary>
    public static string Duration(int seconds) => seconds < 60 ? $"{seconds}\"" : $"{seconds / 60}'{seconds % 60:00}";

    /// <summary>A call timer as a studio timecode: 00:12:41.</summary>
    public static string Timecode(TimeSpan t) => $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}";
}
