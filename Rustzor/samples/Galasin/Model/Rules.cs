namespace Galasin.Model;

/// <summary>
/// The court, in metres. Two columns and two rows of boxes: 9 m across, 12 m long, with a
/// guard line at the start, the middle and the far end, and the sodor's line down the
/// centre. Attackers wait behind the start line (home) and must touch the far side and come
/// back. The same numbers are in the yard engine, which keeps the local player inside them.
/// </summary>
public static class Field
{
    public const double Half = 4.5;
    public const double Length = 12;
    public static readonly double[] Lines = { 0, 6, 12 };
    /// <summary>Behind this an attacker is home: safe, and back home scores a trip.</summary>
    public const double Home = -0.5;
    /// <summary>Past this an attacker has reached the far side and may turn for home.</summary>
    public const double Goal = 12.5;
    public const double BackOfHome = -3.2, BackOfGoal = 15.2;
    /// <summary>How close a guard's hand has to be: standing, and while lunging.</summary>
    public const double Reach = 0.7, LungeReach = 1.15;
    public const double AttackSpeed = 4.4, GuardSpeed = 4.0;
    public const double LungeTime = 0.38, LungeCooldown = 1.3;
    public const int TeamSize = 4;
    public const int Guards = 4;   // three on the lines, one sodor

    public static bool OnCourt(double z) => z > Home && z < Goal;
}

public enum Phase { Lobby, Count, Play, Swap, End }

public enum RoleKind { Attacker, LineGuard, Sodor, Watching }

/// <summary>Where a player stands and what they may do: run anywhere (attacker), slide along one line, or the centre line.</summary>
public readonly record struct Role(RoleKind Kind, int Line)
{
    public string Mode => Kind switch { RoleKind.Attacker => "free", RoleKind.LineGuard => "x", RoleKind.Sodor => "z", _ => "none" };
    public double Speed => Kind == RoleKind.Attacker ? Field.AttackSpeed : Field.GuardSpeed;
    public bool Guarding => Kind is RoleKind.LineGuard or RoleKind.Sodor;
}

/// <summary>One kid in the match: a person in some tab, or a bot the host runs.</summary>
public sealed class Player
{
    // Fields rather than properties: the host reads these hundreds of times a tick, and
    // in the interpreter a field read is much cheaper than a property call.
    public string Id = "";
    public string Name = "";
    public char Team = 'p';
    public bool Bot;
    /// <summary>0, 1, 2: which line this kid guards; 3: the sodor.</summary>
    public int Slot;

    public double X, Z, Yaw;
    /// <summary>Seconds left in a lunge (bots), or 1 while a person's lunge lasts.</summary>
    public double Lunge, LungeCooldown;
    /// <summary>Touched the far side on this trip: the next arrival home scores.</summary>
    public bool Reached;
    /// <summary>Referee only: when this person's tab last sent a position, and whether a bot stands in for them now.</summary>
    public double LastHeard;
    public bool StandIn;

    // a bot's mind
    public double Courage = 1.9, Think, TargetX, TargetZ, Jitter;

    public Role RoleWhen(char attacking) =>
        Team == attacking ? new Role(RoleKind.Attacker, 0)
        : Slot < 3 ? new Role(RoleKind.LineGuard, Slot)
        : new Role(RoleKind.Sodor, 0);
}

/// <summary>A tab in the room, as its lobby card shows it.</summary>
public sealed class Profile
{
    public string Id { get; init; } = "";
    public string Name { get; set; } = "";
    public char Team { get; set; } = 'p';
    public bool Visible { get; set; } = true;
}
