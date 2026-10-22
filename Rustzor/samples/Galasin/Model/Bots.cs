using Rustzor.Game;

namespace Galasin.Model;

/// <summary>
/// The kids the host runs when a team is short of people. Attackers creep up to a line,
/// wait for its guard to be drawn to one side, and dash through the gap on the other;
/// they keep away from the centre line while the sodor is near. Guards shadow whichever
/// attacker is closest to their line, a beat late, and lunge when one comes within reach.
/// They think a few times a second, not every tick, so they can be beaten.
/// </summary>
public static partial class Match
{
    private static readonly Rng Dice = new((ulong)DateTime.UtcNow.Ticks | 1);

    // Who is where this tick, worked out once: the attackers, and the guard on each slot.
    private static readonly List<Player> Attackers = new();
    private static readonly Player?[] GuardBySlot = new Player?[Field.Guards];

    private static void Sort()
    {
        Attackers.Clear();
        for (var i = 0; i < GuardBySlot.Length; i++) GuardBySlot[i] = null;
        foreach (var p in Roster)
        {
            if (p.Team == Attack) Attackers.Add(p);
            else if (p.Slot < Field.Guards) GuardBySlot[p.Slot] = p;
        }
    }

    private static void RunBots(double dt)
    {
        foreach (var b in Roster)
        {
            b.LungeCooldown = Math.Max(0, b.LungeCooldown - dt);
            if (!b.Bot) continue;
            b.Lunge = Math.Max(0, b.Lunge - dt);
            if (b.Team == Attack) RunAttacker(b, dt);
            else if (b.Slot < 3) RunLineGuard(b, Field.Lines[b.Slot], dt);
            else RunSodor(b, dt);
        }
    }

    private static Player? GuardOf(int slot) => GuardBySlot[slot];

    private static void RunAttacker(Player b, double dt)
    {
        var dir = b.Reached ? -1 : 1;
        // the next line in the way, and who keeps it
        var next = -1;
        for (var k = 0; k < 3; k++)
        {
            var i = dir > 0 ? k : 2 - k;
            if ((Field.Lines[i] - b.Z) * dir > -0.35) { next = i; break; }
        }
        double dx = 0, dz = dir;
        var guard = next >= 0 ? GuardOf(next) : null;
        b.Think -= dt;
        if (guard is not null)
        {
            if (b.Think <= 0)
            {
                // pick the side away from the guard, with a little doubt
                var away = guard.X >= b.X ? -1 : 1;
                if (Math.Abs(guard.X - b.X) < 0.8 && Dice.Chance(0.5)) away = -away;
                b.TargetX = away * Dice.Range(2.4, 4.1);
                b.Think = Dice.Range(0.35, 0.9);
                b.Jitter = Dice.Range(-0.25, 0.25);
            }
            var gap = Math.Abs(guard.X - b.X);
            var ahead = (Field.Lines[next] - b.Z) * dir;
            if (ahead < 1.6)
            {
                if (gap > b.Courage + b.Jitter)
                {
                    dz = dir;                                   // through, now
                    dx = Math.Sign(b.X - guard.X) * 0.25;
                }
                else
                {
                    dz = ahead < 1.0 ? -dir * 0.7 : 0;          // hang back out of reach
                    dx = Math.Sign(b.TargetX - b.X);
                }
            }
            else
            {
                dz = dir;
                dx = MathX.Clamp((b.TargetX - b.X) * 0.45, -0.75, 0.75);
            }
        }
        // the sodor guards the centre line: keep off it while the sodor is close
        var sodor = GuardOf(3);
        if (sodor is not null && Math.Abs(b.X) < 1.35 && Math.Abs(sodor.Z - b.Z) < 1.9 && Field.OnCourt(b.Z))
        {
            dx = b.X >= 0 ? 1 : -1;
            dz *= 0.45;
        }
        Move(b, dx, dz, Field.AttackSpeed * 0.9, dt);
        b.X = MathX.Clamp(b.X, -Field.Half + 0.15, Field.Half - 0.15);
        b.Z = MathX.Clamp(b.Z, Field.BackOfHome, Field.BackOfGoal);
    }

    /// <summary>The attacker a guard should worry about: on the court (or just behind its end lines), nearest by a weighted distance.</summary>
    private static Player? Threat(Player guard, bool sodor, double within)
    {
        Player? best = null;
        var bestD = within;
        foreach (var a in Attackers)
        {
            if (!(a.Z > -1.2 && a.Z < Field.Length + 1.2)) continue;
            var d = sodor
                ? Math.Abs(a.X) * 1.4 + Math.Abs(a.Z - guard.Z) * 0.3
                : Math.Abs(a.Z - guard.Z) + Math.Abs(a.X - guard.X) * 0.25;
            if (d < bestD) { bestD = d; best = a; }
        }
        return best;
    }

    private static void RunLineGuard(Player b, double line, double dt)
    {
        b.Z = line;
        b.Think -= dt;
        if (b.Think <= 0)
        {
            var t = Threat(b, false, 7);
            b.TargetX = t is null ? 0 : t.X + Dice.Range(-0.3, 0.3);
            b.Think = Dice.Range(0.12, 0.26);                   // a beat late, every time
        }
        var dx = Math.Abs(b.TargetX - b.X) > 0.08 ? Math.Sign(b.TargetX - b.X) : 0;
        Move(b, dx, 0, Field.GuardSpeed * 0.92, dt);
        b.X = MathX.Clamp(b.X, -Field.Half, Field.Half);
        b.Z = line;
        TryLunge(b);
    }

    private static void RunSodor(Player b, double dt)
    {
        b.X = 0;
        b.Think -= dt;
        if (b.Think <= 0)
        {
            var t = Threat(b, true, 6);
            b.TargetZ = t is null ? Field.Length / 2 : t.Z;
            b.Think = Dice.Range(0.14, 0.3);
        }
        var dz = Math.Abs(b.TargetZ - b.Z) > 0.08 ? Math.Sign(b.TargetZ - b.Z) : 0;
        Move(b, 0, dz, Field.GuardSpeed * 0.9, dt);
        b.Z = MathX.Clamp(b.Z, 0, Field.Length);
        b.X = 0;
        TryLunge(b);
    }

    private static void TryLunge(Player b)
    {
        if (b.LungeCooldown > 0 || b.Lunge > 0) return;
        foreach (var a in Attackers)
        {
            if (!Field.OnCourt(a.Z)) continue;
            var dx = a.X - b.X;
            var dz = a.Z - b.Z;
            if (dx * dx + dz * dz < 1.05 * 1.05)
            {
                b.Lunge = Field.LungeTime;
                b.LungeCooldown = Field.LungeCooldown;
                return;
            }
        }
    }

    private static void Move(Player b, double dx, double dz, double speed, double dt)
    {
        var l = Math.Sqrt(dx * dx + dz * dz);
        if (l < 1e-6)
        {
            if (b.Team != Attack) b.Yaw = TurnTo(b.Yaw, Math.PI, dt * 5);
            return;
        }
        if (l > 1) { dx /= l; dz /= l; }
        if (b.Lunge > 0) speed *= 0.55;
        b.X += dx * speed * dt;
        b.Z += dz * speed * dt;
        b.Yaw = TurnTo(b.Yaw, Math.Atan2(dx, dz), dt * 12);
    }

    private static double TurnTo(double from, double to, double rate)
    {
        var d = MathX.Wrap(to - from, -Math.PI, Math.PI);
        return from + d * Math.Min(1, rate);
    }
}
