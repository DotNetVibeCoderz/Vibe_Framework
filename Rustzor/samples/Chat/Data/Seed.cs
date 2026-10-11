using System.Text;

namespace Chat.Data;

/// <summary>Two days of a small product studio shipping release 2.4.</summary>
public static class Seed
{
    public static List<Message> Messages()
    {
        var list = new List<Message>();
        var today = DateTime.Today;
        var n = 0;
        Message Add(string room, string author, string text, DateTime at, string parent = "", string image = "")
        {
            var m = new Message { Id = $"seed{++n:000}", Room = room, Author = author, Text = text, Time = at.ToString("yyyy-MM-ddTHH:mm:ss"), Parent = parent, Image = image };
            list.Add(m);
            return m;
        }
        void React(Message m, string emoji, params string[] who) => m.Reactions[emoji] = who.ToList();

        var y = today.AddDays(-1);
        var hello = Add("umum", "sari", "Selamat pagi, semua! Release 2.4 is this Thursday. The plan and the owners are pinned in #rilis.", y.AddHours(8).AddMinutes(52));
        React(hello, "👍", "ayu", "bima", "raka");
        hello.Pinned = true;
        Add("umum", "bima", "Pagi! Coffee machine on the 3rd floor is fixed, by the way ☕", y.AddHours(9).AddMinutes(5));
        var lunch = Add("random", "citra", "Lunch at Warung Bu Sri today? Their sayur lodeh is back on the menu.", y.AddHours(11).AddMinutes(40));
        React(lunch, "😂", "bima");
        React(lunch, "❤️", "ayu", "dewi");
        Add("random", "ayu", "Count me in. 12:15 at the lobby?", y.AddHours(11).AddMinutes(43), lunch.Id);
        Add("random", "bima", "Same. I'll book a table for six.", y.AddHours(11).AddMinutes(47), lunch.Id);

        var checkout = Add("desain", "ayu", "New checkout flow, round two. I moved the order summary above the payment methods and cut a step. Thoughts?", y.AddHours(14).AddMinutes(10), image: Mock());
        React(checkout, "👀", "dewi", "sari");
        React(checkout, "🎉", "citra");
        Add("desain", "dewi", "Much better. The total is now readable at a glance. Two things: the promo field can collapse by default, and the primary button needs the amount on it.", y.AddHours(14).AddMinutes(32), checkout.Id);
        Add("desain", "ayu", "Agreed on both. Updating now.", y.AddHours(14).AddMinutes(35), checkout.Id);
        Add("desain", "sari", "Customers keep asking where the promo code goes, so collapsed is fine as long as the link says **Have a promo code?**", y.AddHours(14).AddMinutes(41), checkout.Id);
        Add("desain", "dewi", "Reminder: design critique moves to Friday 10:00 this week because of the release.", y.AddHours(16).AddMinutes(2));

        var plan = Add("rilis", "sari", "Release 2.4 plan:\n1. Checkout flow (Ayu, Bima)\n2. Order history API (Raka)\n3. Launch email (Citra)\nCode freeze Wednesday 17:00, deploy Thursday 10:00.", y.AddHours(9).AddMinutes(30));
        plan.Pinned = true;
        React(plan, "👍", "ayu", "bima", "citra", "raka");
        Add("rilis", "raka", "Order history API is on staging. Pagination is cursor-based, see `GET /v2/orders?cursor=`.", y.AddHours(15).AddMinutes(12));
        var bug = Add("rilis", "bima", "Found a bug: the payment step loses the selected bank when you go back a step. Fixing it today.", y.AddHours(17).AddMinutes(20));
        React(bug, "🙏", "sari");
        Add("rilis", "raka", "Thanks. If you need the session to persist, the cart endpoint keeps it for 30 minutes.", y.AddHours(17).AddMinutes(26), bug.Id);

        Add("umum", "citra", "The launch email draft is ready for review: subject line options in the doc. Please vote by tomorrow noon 🙏", today.AddHours(8).AddMinutes(40));
        Add("umum", "raka", "Heads-up: staging will be down 10 minutes at 11:00 for the database migration.", today.AddHours(9).AddMinutes(15));
        var fixedBug = Add("rilis", "bima", "Bank selection bug is fixed and on staging. @Sari ready for QA.", today.AddHours(9).AddMinutes(48));
        React(fixedBug, "🎉", "sari", "ayu");
        Add("rilis", "sari", "Great, I'll run through the checklist after stand-up.", today.AddHours(9).AddMinutes(55), fixedBug.Id);
        Add("desain", "dewi", "Updated icons are in the library: 1.8 px stroke, 24 px grid. Please swap the old cart icon when you touch a screen.", today.AddHours(10).AddMinutes(20));

        Add(ChatStore.DmId("ayu", "dewi"), "dewi", "Hi Ayu, can you send me the final checkout screens before 15:00? I want to include them in Friday's critique.", today.AddHours(10).AddMinutes(5));
        Add(ChatStore.DmId("bima", "raka"), "raka", "Bima, the order history endpoint now returns `status_label` too, so you don't have to map codes on the client.", today.AddHours(10).AddMinutes(12));
        Add(ChatStore.DmId("citra", "sari"), "sari", "Citra, could you add the new payment banks to the launch email? The list is in #rilis.", today.AddHours(10).AddMinutes(30));
        return list;
    }

    /// <summary>A small checkout mock-up as an SVG image, standing in for a screenshot.</summary>
    private static string Mock()
    {
        const string svg = """
            <svg xmlns="http://www.w3.org/2000/svg" width="480" height="300" viewBox="0 0 480 300">
              <rect width="480" height="300" rx="16" fill="#f6f2fb"/>
              <rect x="24" y="24" width="270" height="252" rx="12" fill="#fff" stroke="#e3d9f2"/>
              <text x="44" y="58" font-family="sans-serif" font-size="16" font-weight="700" fill="#2a1d3d">Payment</text>
              <rect x="44" y="76" width="230" height="40" rx="8" fill="#fff" stroke="#6b3fa0" stroke-width="2"/>
              <text x="60" y="101" font-family="sans-serif" font-size="13" fill="#2a1d3d">BCA Virtual Account</text>
              <rect x="44" y="126" width="230" height="40" rx="8" fill="#fff" stroke="#e3d9f2"/>
              <text x="60" y="151" font-family="sans-serif" font-size="13" fill="#6d6280">QRIS</text>
              <rect x="44" y="176" width="230" height="40" rx="8" fill="#fff" stroke="#e3d9f2"/>
              <text x="60" y="201" font-family="sans-serif" font-size="13" fill="#6d6280">Credit card</text>
              <rect x="44" y="230" width="230" height="30" rx="8" fill="#6b3fa0"/>
              <text x="159" y="250" text-anchor="middle" font-family="sans-serif" font-size="13" font-weight="700" fill="#fff">Pay Rp 186.000</text>
              <rect x="314" y="24" width="142" height="160" rx="12" fill="#fff" stroke="#e3d9f2"/>
              <text x="330" y="54" font-family="sans-serif" font-size="13" font-weight="700" fill="#2a1d3d">Order summary</text>
              <rect x="330" y="70" width="110" height="8" rx="4" fill="#e3d9f2"/>
              <rect x="330" y="88" width="80" height="8" rx="4" fill="#e3d9f2"/>
              <rect x="330" y="106" width="96" height="8" rx="4" fill="#e3d9f2"/>
              <text x="330" y="160" font-family="sans-serif" font-size="15" font-weight="700" fill="#2a1d3d">Rp 186.000</text>
            </svg>
            """;
        return "data:image/svg+xml;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes(svg));
    }
}
