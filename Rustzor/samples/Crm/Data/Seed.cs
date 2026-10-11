using Rustzor.Data;

namespace Crm.Data;

/// <summary>A year of plausible CRM data for a coffee and kitchen-equipment distributor.</summary>
internal static class Seed
{
    private static readonly (string Name, string Industry, string City, string Tier)[] Companies =
    {
        ("Hotel Sanur Indah", "Hospitality", "Denpasar", "A"), ("Kopi Kenangan Raya", "Retail", "Jakarta", "A"),
        ("Pabrik Roti Merdeka", "Manufacturing", "Bandung", "B"), ("RS Harapan Sehat", "Healthcare", "Surabaya", "B"),
        ("Gudang Nusantara Logistik", "Logistics", "Semarang", "B"), ("Universitas Parahyangan Kantin", "Education", "Bandung", "C"),
        ("Café Braga Lama", "Hospitality", "Bandung", "C"), ("Mart Sembilan", "Retail", "Medan", "B"),
        ("Resort Lombok Biru", "Hospitality", "Mataram", "A"), ("Klinik Pratama Sentosa", "Healthcare", "Yogyakarta", "C"),
        ("Toko Kue Delapan", "Retail", "Makassar", "C"), ("Pelabuhan Catering Tanjung", "Logistics", "Surabaya", "B"),
    };

    private static readonly string[] FirstNames = { "Agus", "Budi", "Dewi", "Eka", "Fajar", "Gita", "Hendra", "Indah", "Joko", "Kartika", "Lina", "Made", "Nanda", "Putri", "Rizky", "Sari", "Teguh", "Wulan" };
    private static readonly string[] LastNames = { "Saputra", "Wijaya", "Halim", "Kusuma", "Pratama", "Siregar", "Nasution", "Tanjung", "Hidayat", "Gunawan" };
    private static readonly string[] Titles = { "Purchasing Manager", "Owner", "Head Chef", "Operations Lead", "Finance Director", "F&B Manager" };
    private static readonly string[] Products = { "Espresso machines", "Coffee bean supply", "Kitchen refit", "Cold-chain fridges", "Barista training", "POS terminals", "Annual service plan" };

    public static async Task RunAsync(SqliteDb db)
    {
        var rng = new Random(2026);
        var today = DateTime.Today;
        await db.TransactionAsync(async tx =>
        {
            for (var i = 0; i < Companies.Length; i++)
            {
                var c = Companies[i];
                var id = i + 1;
                var owner = Catalog.Owners[i % Catalog.Owners.Length];
                await tx.RunAsync("insert into companies (id, name, industry, city, owner, website, tier) values (?, ?, ?, ?, ?, ?, ?)",
                    id, c.Name, c.Industry, c.City, owner, "www." + new string(c.Name.ToLowerInvariant().Where(char.IsLetter).Take(14).ToArray()) + ".co.id", c.Tier);

                for (var k = 0; k < 2 + rng.Next(3); k++)
                {
                    var name = $"{FirstNames[rng.Next(FirstNames.Length)]} {LastNames[rng.Next(LastNames.Length)]}";
                    await tx.RunAsync("insert into contacts (companyId, name, title, email, phone) values (?, ?, ?, ?, ?)",
                        id, name, Titles[rng.Next(Titles.Length)], name.ToLowerInvariant().Replace(' ', '.') + "@mail.co.id", $"+62 8{rng.Next(11, 99)} {rng.Next(1000, 9999)} {rng.Next(1000, 9999)}");
                }

                for (var k = 0; k < 2 + rng.Next(4); k++)
                {
                    var created = today.AddDays(-rng.Next(10, 300));
                    var stage = Catalog.Stages[Math.Min(Catalog.Stages.Length - 1, (int)(Math.Pow(rng.NextDouble(), 0.8) * Catalog.Stages.Length))].Id;
                    var close = stage is "won" or "lost" ? created.AddDays(rng.Next(15, 90)) : today.AddDays(rng.Next(-10, 120));
                    var value = Math.Round((decimal)(5 + rng.NextDouble() * (c.Tier == "A" ? 400 : c.Tier == "B" ? 180 : 60)), 1) * 1_000_000m;
                    await tx.RunAsync("insert into deals (companyId, title, value, stage, owner, source, created, closeDate) values (?, ?, ?, ?, ?, ?, ?, ?)",
                        id, Products[rng.Next(Products.Length)], (double)value, stage, owner, Catalog.Sources[rng.Next(Catalog.Sources.Length)],
                        created.ToString("yyyy-MM-dd"), close.ToString("yyyy-MM-dd"));
                }

                for (var k = 0; k < 2 + rng.Next(3); k++)
                {
                    var kind = Catalog.ActivityKinds[rng.Next(Catalog.ActivityKinds.Length)];
                    var due = today.AddDays(rng.Next(-12, 18));
                    var subject = kind switch
                    {
                        "call" => "Follow-up call about pricing",
                        "meeting" => "Site visit and demo",
                        "email" => "Send revised quotation",
                        _ => "Prepare contract draft",
                    };
                    await tx.RunAsync("insert into activities (companyId, dealId, kind, subject, due, done, owner) values (?, 0, ?, ?, ?, ?, ?)",
                        id, kind, subject, due.ToString("yyyy-MM-dd"), due < today.AddDays(-5) ? 1 : 0, owner);
                }
            }
        });
        await db.SaveAsync();
    }
}
