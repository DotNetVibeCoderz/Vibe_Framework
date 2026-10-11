using Rustzor.Browser.Storage;
using Rustzor.Interop;

namespace {{Namespace}};

/// <summary>What is saved per fact once it has been asked: small, so saving stays fast.</summary>
public sealed class Progress
{
    public string Key { get; set; } = "";
    public int Box { get; set; }
    public int Seen { get; set; }
    public int Right { get; set; }
    public int DueRound { get; set; }
    public double BestSeconds { get; set; }
}

public sealed class Fact
{
    public string Op { get; set; } = "×";
    public int A { get; set; }
    public int B { get; set; }
    /// <summary>Leitner box 0–4. A right answer moves a card up, a wrong one back to 0.</summary>
    public int Box { get; set; }
    public int Seen { get; set; }
    public int Right { get; set; }
    /// <summary>Round after which the card is due again.</summary>
    public int DueRound { get; set; }
    public double BestSeconds { get; set; }

    public string Key => $"{A}{Op}{B}";
    public string Prompt => $"{A} {Op} {B}";
    public int Answer => Op switch { "+" => A + B, "−" => A - B, "×" => A * B, _ => A / B };
}

/// <summary>
/// All cards for the chosen operations, and the schedule. Box n comes back
/// after 2ⁿ rounds, so known facts fade out and missed ones return at once.
/// </summary>
public sealed class Deck
{
    private const string Key = "flashcards";
    public static readonly string[] Ops = { "×", "÷", "+", "−" };

    public List<Fact> Cards { get; private set; } = new();
    public int Round { get; private set; }

    public static Deck Load()
    {
        var deck = new Deck();
        deck.Build();
        deck.Round = int.TryParse(WebStorage.Local.Get(Key + "-round"), out var r) ? r : 0;
        var byKey = deck.Cards.ToDictionary(c => c.Key);
        foreach (var p in WebStorage.Local.GetList<Progress>(Key))
        {
            if (!byKey.TryGetValue(p.Key, out var c)) continue;
            c.Box = p.Box; c.Seen = p.Seen; c.Right = p.Right; c.DueRound = p.DueRound; c.BestSeconds = p.BestSeconds;
        }
        return deck;
    }

    private void Build()
    {
        for (var a = 1; a <= 12; a++)
            for (var b = 1; b <= 12; b++)
            {
                Cards.Add(new Fact { Op = "×", A = a, B = b });
                Cards.Add(new Fact { Op = "÷", A = a * b, B = b });
            }
        for (var a = 0; a <= 20; a++)
            for (var b = 0; b <= 20 - a; b++)
            {
                Cards.Add(new Fact { Op = "+", A = a, B = b });
                Cards.Add(new Fact { Op = "−", A = a + b, B = b });
            }
    }

    /// <summary>The next card to ask: due cards from the lowest box first, a little shuffled.</summary>
    public Fact Next(ICollection<string> ops, Random rng, Fact? previous)
    {
        var pool = Cards.Where(c => ops.Contains(c.Op) && c != previous).ToList();
        var due = pool.Where(c => c.DueRound <= Round).ToList();
        if (due.Count == 0) due = pool;
        var lowest = due.Min(c => c.Box);
        var candidates = due.Where(c => c.Box <= lowest + 1).ToList();
        return candidates[rng.Next(candidates.Count)];
    }

    public void Record(Fact card, bool right, double seconds)
    {
        Round++;
        card.Seen++;
        if (right)
        {
            card.Right++;
            card.Box = Math.Min(4, card.Box + (seconds < 3 ? 1 : 0) + (card.Box == 0 ? 1 : 0));
            if (card.BestSeconds == 0 || seconds < card.BestSeconds) card.BestSeconds = Math.Round(seconds, 1);
        }
        else
        {
            card.Box = 0;
        }
        card.DueRound = Round + (1 << card.Box);
        Save();
    }

    public void Reset()
    {
        Cards.Clear();
        Round = 0;
        Build();
        Save();
    }

    private void Save()
    {
        var seen = Cards.Where(c => c.Seen > 0)
            .Select(c => new Progress { Key = c.Key, Box = c.Box, Seen = c.Seen, Right = c.Right, DueRound = c.DueRound, BestSeconds = c.BestSeconds })
            .ToList();
        WebStorage.Local.SetJson(Key, seen);
        WebStorage.Local.Set(Key + "-round", Round.ToString());
    }
}
