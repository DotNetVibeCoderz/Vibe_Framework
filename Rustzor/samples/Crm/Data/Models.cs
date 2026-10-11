namespace Crm.Data;

public sealed class Company
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string Industry { get; set; } = "";
    public string City { get; set; } = "";
    public string Owner { get; set; } = "";
    public string Website { get; set; } = "";
    public string Tier { get; set; } = "B";
    public double OpenValue { get; set; }
    public long Deals { get; set; }
}

public sealed class Contact
{
    public long Id { get; set; }
    public long CompanyId { get; set; }
    public string Name { get; set; } = "";
    public string Title { get; set; } = "";
    public string Email { get; set; } = "";
    public string Phone { get; set; } = "";
    public string Company { get; set; } = "";
}

public sealed class Deal
{
    public long Id { get; set; }
    public long CompanyId { get; set; }
    public string Title { get; set; } = "";
    public decimal Value { get; set; }
    public string Stage { get; set; } = "lead";
    public string Owner { get; set; } = "";
    public string Source { get; set; } = "";
    public string Created { get; set; } = "";
    public string CloseDate { get; set; } = "";
    public string Company { get; set; } = "";
}

public sealed class Activity
{
    public long Id { get; set; }
    public long CompanyId { get; set; }
    public long DealId { get; set; }
    public string Kind { get; set; } = "call";
    public string Subject { get; set; } = "";
    public string Due { get; set; } = "";
    public long Done { get; set; }
    public string Owner { get; set; } = "";
    public string Company { get; set; } = "";
}

public sealed record Stage(string Id, string Name, double Probability, string Tone);

public static class Catalog
{
    public static readonly Stage[] Stages =
    {
        new("lead", "Lead", 0.10, "slate"),
        new("qualified", "Qualified", 0.25, "patina"),
        new("proposal", "Proposal", 0.50, "indigo"),
        new("negotiation", "Negotiation", 0.75, "turmeric"),
        new("won", "Won", 1.00, "good"),
        new("lost", "Lost", 0.00, "bad"),
    };

    public static readonly string[] Owners = { "Ayu Lestari", "Bima Santoso", "Citra Wijaya", "Dimas Prakoso" };
    public static readonly string[] Sources = { "Referral", "Trade show", "Website", "Outbound", "Partner" };
    public static readonly string[] Industries = { "Hospitality", "Retail", "Manufacturing", "Healthcare", "Logistics", "Education" };
    public static readonly string[] ActivityKinds = { "call", "meeting", "email", "task" };

    public static Stage StageOf(string id) => Stages.FirstOrDefault(s => s.Id == id) ?? Stages[0];

    public static string Money(decimal v) =>
        v >= 1_000_000_000m ? $"Rp {v / 1_000_000_000m:0.0} M" : v >= 1_000_000m ? $"Rp {v / 1_000_000m:0.#} jt" : $"Rp {v:N0}";

    public static string Money(double v) => Money((decimal)v);

    public static string KindIcon(string kind) => kind switch { "call" => "phone", "meeting" => "users", "email" => "mail", _ => "check" };
}
