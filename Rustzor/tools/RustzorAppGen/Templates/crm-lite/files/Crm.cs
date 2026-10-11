using Rustzor.Browser.Storage;

namespace {{Namespace}};

public sealed class Contact
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Company { get; set; } = "";
    public string Email { get; set; } = "";
    public string Phone { get; set; } = "";
    public string Source { get; set; } = "Referral";
    /// <summary>yyyy-MM-dd, or empty when nothing is planned.</summary>
    public string FollowUp { get; set; } = "";
}

public sealed class Deal
{
    public int Id { get; set; }
    public int ContactId { get; set; }
    public string Title { get; set; } = "";
    public double Value { get; set; }
    public string Stage { get; set; } = "lead";
    public string Updated { get; set; } = "";
}

public sealed record Stage(string Id, string Title, double Probability);

/// <summary>The whole CRM: two lists kept in localStorage.</summary>
public sealed class Crm
{
    public static readonly Stage[] Stages =
    {
        new("lead", "Lead", 0.1), new("qualified", "Qualified", 0.3), new("proposal", "Proposal", 0.6),
        new("won", "Won", 1), new("lost", "Lost", 0),
    };

    public static readonly string[] Sources = { "Referral", "Website", "Event", "Cold outreach", "Partner" };

    public static Crm Instance { get; } = Load();

    public List<Contact> Contacts { get; private set; } = new();
    public List<Deal> Deals { get; private set; } = new();

    public Contact? ContactOf(Deal d) => Contacts.FirstOrDefault(c => c.Id == d.ContactId);
    public IEnumerable<Deal> DealsOf(Contact c) => Deals.Where(d => d.ContactId == c.Id);
    public IEnumerable<Deal> Open => Deals.Where(d => d.Stage is not ("won" or "lost"));
    public double Weighted => Open.Sum(d => d.Value * Stages.First(s => s.Id == d.Stage).Probability);

    public void Save(Contact c)
    {
        if (c.Id == 0) { c.Id = Contacts.Select(x => x.Id).DefaultIfEmpty(0).Max() + 1; Contacts.Add(c); }
        Persist();
    }

    public void Save(Deal d)
    {
        d.Updated = DateTime.Today.ToString("yyyy-MM-dd");
        if (d.Id == 0) { d.Id = Deals.Select(x => x.Id).DefaultIfEmpty(0).Max() + 1; Deals.Add(d); }
        Persist();
    }

    public void Delete(Contact c)
    {
        Contacts.Remove(c);
        Deals.RemoveAll(d => d.ContactId == c.Id);
        Persist();
    }

    private void Persist()
    {
        WebStorage.Local.SetJson("crm-contacts", Contacts);
        WebStorage.Local.SetJson("crm-deals", Deals);
    }

    private static Crm Load()
    {
        var crm = new Crm
        {
            Contacts = WebStorage.Local.GetList<Contact>("crm-contacts"),
            Deals = WebStorage.Local.GetList<Deal>("crm-deals"),
        };
        if (crm.Contacts.Count == 0) Seed(crm);
        return crm;
    }

    private static void Seed(Crm crm)
    {
        var today = DateTime.Today;
        string Day(int offset) => today.AddDays(offset).ToString("yyyy-MM-dd");
        (string Name, string Company, string Source, int FollowUp)[] people =
        {
            ("Sari Wulandari", "Kopi Nusantara", "Referral", 1), ("Daniel Okafor", "Lagos Freight", "Website", -2),
            ("Mei Tanaka", "Hoshi Robotics", "Event", 3), ("Lucas Moreau", "Atelier Moreau", "Partner", 0),
            ("Priya Raman", "Chennai Textiles", "Cold outreach", 7), ("Tom Becker", "Becker Bau", "Website", -5),
            ("Ana Lima", "Verde Foods", "Referral", 12), ("Rizky Pratama", "Batik Digital", "Event", 2),
        };
        foreach (var p in people)
        {
            var slug = p.Name.Split(' ')[0].ToLowerInvariant();
            crm.Contacts.Add(new Contact
            {
                Id = crm.Contacts.Count + 1, Name = p.Name, Company = p.Company, Source = p.Source,
                Email = $"{slug}@{p.Company.Replace(" ", "").ToLowerInvariant()}.example", Phone = $"+62 812 {3400 + crm.Contacts.Count * 137:0000} {1200 + crm.Contacts.Count * 71:0000}",
                FollowUp = p.FollowUp == 12 ? "" : Day(p.FollowUp),
            });
        }
        (int Contact, string Title, double Value, string Stage)[] deals =
        {
            (1, "Café POS rollout", 18_000, "proposal"), (1, "Loyalty app", 9_500, "lead"), (2, "Fleet tracking", 42_000, "qualified"),
            (3, "Warehouse robots pilot", 120_000, "proposal"), (4, "Online showroom", 14_000, "won"), (5, "ERP integration", 65_000, "lead"),
            (6, "Site safety tablets", 22_000, "lost"), (7, "Recipe costing tool", 7_800, "won"), (8, "Batik marketplace", 31_000, "qualified"),
            (3, "Maintenance contract", 24_000, "won"),
        };
        foreach (var d in deals)
            crm.Deals.Add(new Deal { Id = crm.Deals.Count + 1, ContactId = d.Contact, Title = d.Title, Value = d.Value, Stage = d.Stage, Updated = Day(-crm.Deals.Count * 3) });
        crm.Persist();
    }
}
