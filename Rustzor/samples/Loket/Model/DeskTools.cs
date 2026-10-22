using System.Globalization;
using System.Text;
using Rustzor.AI;
using Rustzor.Interop;

namespace Loket.Model;

/// <summary>The four things the desk can do besides talking, as tools any model can call.</summary>
public static class DeskTools
{
    public static readonly ChatTool Calculator = new("calculator",
        "Evaluate an arithmetic expression exactly: + - * / % ^ and parentheses, decimals with a dot. Use it for any sum, fee, percentage or date arithmetic instead of computing in your head.",
        """{"type":"object","properties":{"expression":{"type":"string","description":"For example (2400000 * 15) / 100"}},"required":["expression"]}""");

    public static readonly ChatTool CurrentTime = ChatTool.NoArguments("current_time",
        "The current date and time in Western Indonesia Time (WIB), and whether the office is open right now.");

    public static readonly ChatTool SearchDocuments = new("search_documents",
        "Search the office's own documents (service procedures, requirements, fees, opening hours). Always use it before answering a question about a service. Returns numbered passages; cite them in the answer as [1], [2].",
        """{"type":"object","properties":{"query":{"type":"string","description":"What to look for, in the resident's words"}},"required":["query"]}""");

    public static readonly ChatTool CreateNote = new("create_note",
        "Write down a follow-up task for the front-desk staff, for example a document a resident will bring back or a call to make.",
        """{"type":"object","properties":{"title":{"type":"string"},"detail":{"type":"string"},"due":{"type":"string","description":"When, in words, if the resident said"}},"required":["title"]}""");

    /// <summary>A tool agent for one answer, with the search results numbered on from <paramref name="sources"/>.</summary>
    public static ToolAgent Agent(IToolChatModel model, List<Source> sources, string convId)
    {
        var agent = new ToolAgent(model) { MaxSteps = 5 };
        agent.Register(Calculator, call =>
        {
            var expression = call.Args["expression"].AsStringOrNull() ?? "";
            var value = Calc.Evaluate(expression);
            return $"{{\"expression\":{Quote(expression)},\"result\":{Quote(Calc.Format(value))}}}";
        });
        agent.Register(CurrentTime, _ => Clock());
        agent.Register(SearchDocuments, call => Search(call.Args["query"].AsStringOrNull() ?? "", sources));
        agent.Register(CreateNote, call =>
        {
            var title = (call.Args["title"].AsStringOrNull() ?? "").Trim();
            if (title.Length == 0) throw new InvalidOperationException("a note needs a title");
            var note = Desk.AddNote(title, call.Args["detail"].AsStringOrNull() ?? "", call.Args["due"].AsStringOrNull() ?? "", convId);
            return $"{{\"saved\":true,\"id\":{Quote(note.Id)},\"title\":{Quote(note.Title)}}}";
        });
        return agent;
    }

    public static string Search(string query, List<Source> sources)
    {
        var hits = Knowledge.Search(query, 3);
        if (hits.Count == 0) return "No passage in the office documents matches this. Say so, and suggest asking at the counter.";
        var sb = new StringBuilder();
        foreach (var hit in hits)
        {
            var existing = sources.FirstOrDefault(s => s.DocId == hit.Chunk.DocId && s.Chunk == hit.Chunk.Index);
            var n = existing?.N ?? sources.Count + 1;
            if (existing is null)
            {
                sources.Add(new Source
                {
                    N = n,
                    DocId = hit.Chunk.DocId,
                    DocTitle = hit.Chunk.DocTitle,
                    Section = hit.Chunk.Section,
                    Chunk = hit.Chunk.Index,
                    Text = hit.Chunk.Text,
                });
            }
            sb.Append('[').Append(n).Append("] ").Append(hit.Chunk.DocTitle);
            if (hit.Chunk.Section.Length > 0) sb.Append(" — ").Append(hit.Chunk.Section);
            sb.Append(": ").Append(hit.Chunk.Text.Replace("\n", " ")).Append("\n\n");
        }
        return sb.ToString().Trim();
    }

    private static readonly string[] DaysId = { "Minggu", "Senin", "Selasa", "Rabu", "Kamis", "Jumat", "Sabtu" };
    private static readonly string[] MonthsId = { "Januari", "Februari", "Maret", "April", "Mei", "Juni", "Juli", "Agustus", "September", "Oktober", "November", "Desember" };

    public static string Clock() => Clock(Desk.Wib);

    public static string Clock(DateTime wib)
    {
        var day = (int)wib.DayOfWeek;
        var minutes = wib.Hour * 60 + wib.Minute;
        var (open, note) = OfficeState(day, minutes);
        var date = $"{DaysId[day]}, {wib.Day} {MonthsId[wib.Month - 1]} {wib.Year}";
        var time = wib.Hour.ToString("00") + "." + wib.Minute.ToString("00");
        return $"{{\"date\":{Quote(date)},\"time\":{Quote(time + " WIB")},\"iso\":{Quote(wib.ToString("yyyy-MM-ddTHH:mm") + "+07:00")},\"office_open\":{(open ? "true" : "false")},\"office\":{Quote(note)}}}";
    }

    /// <summary>The opening hours in "Jam Layanan dan Kontak", as code.</summary>
    public static (bool Open, string Note) OfficeState(int day, int minutes)
    {
        if (day is 0 or 6) return (false, "closed at weekends; opens Monday 08.00 WIB");
        var close = day == 5 ? 15 * 60 + 30 : 15 * 60;
        var (breakStart, breakEnd) = day == 5 ? (11 * 60 + 30, 13 * 60) : (12 * 60, 13 * 60);
        if (minutes < 8 * 60) return (false, "opens at 08.00 WIB today; queue numbers from 07.45");
        if (minutes >= close) return (false, day == 5 ? "closed for today; opens Monday 08.00 WIB" : "closed for today; opens tomorrow 08.00 WIB");
        if (minutes >= breakStart && minutes < breakEnd) return (false, $"lunch break until 13.00 WIB");
        return (true, $"open until {close / 60:00}.{close % 60:00} WIB");
    }

    public static string Quote(string s)
    {
        var w = new JsonWriter();
        w.String(s);
        return w.ToString();
    }
}

/// <summary>An exact little calculator: + - * / % ^, parentheses, unary minus, decimals.</summary>
public static class Calc
{
    public static decimal Evaluate(string expression)
    {
        var p = new Parser(Normalize(expression));
        var value = p.Expression();
        p.SkipSpace();
        if (!p.AtEnd) throw new FormatException($"unexpected '{p.Rest}' in the expression");
        return value;
    }

    public static string Format(decimal value)
    {
        var rounded = Math.Round(value, 6);
        return rounded == Math.Truncate(rounded)
            ? Math.Truncate(rounded).ToString(CultureInfo.InvariantCulture)
            : rounded.ToString(CultureInfo.InvariantCulture).TrimEnd('0').TrimEnd('.');
    }

    /// <summary>Accepts what people type: × and x for times, ÷ and : for divide, and Indonesian thousands dots (2.400.000).</summary>
    public static string Normalize(string s)
    {
        var sb = new StringBuilder();
        foreach (var ch in s) sb.Append(ch switch { '×' or 'x' or 'X' => '*', '÷' or ':' => '/', '−' => '-', _ => ch });
        var text = sb.ToString();
        // A dot followed by exactly three digits, twice or more in a number, is a thousands separator.
        var outText = new StringBuilder();
        var i = 0;
        while (i < text.Length)
        {
            if (char.IsDigit(text[i]))
            {
                var start = i;
                while (i < text.Length && (char.IsDigit(text[i]) || text[i] == '.' || text[i] == ',')) i++;
                var number = text[start..i];
                var groups = number.Split('.');
                if (groups.Length > 2 && groups.Skip(1).All(g => g.Length == 3 && g.All(char.IsDigit)))
                    number = string.Concat(groups);
                else if (groups.Length == 2 && groups[1].Length == 3 && number.Contains(',') == false && groups[0].Length <= 3 && groups[0] != "0")
                    number = string.Concat(groups); // 2.500 = 2500, as written in Indonesia
                outText.Append(number.Replace(',', '.'));
                continue;
            }
            outText.Append(text[i]);
            i++;
        }
        return outText.ToString();
    }

    private sealed class Parser(string text)
    {
        private int _i;
        public bool AtEnd => _i >= text.Length;
        public string Rest => text[_i..];

        public void SkipSpace()
        {
            while (_i < text.Length && char.IsWhiteSpace(text[_i])) _i++;
        }

        private bool Eat(char c)
        {
            SkipSpace();
            if (_i < text.Length && text[_i] == c)
            {
                _i++;
                return true;
            }
            return false;
        }

        public decimal Expression()
        {
            var v = Term();
            while (true)
            {
                if (Eat('+')) v += Term();
                else if (Eat('-')) v -= Term();
                else return v;
            }
        }

        private decimal Term()
        {
            var v = Power();
            while (true)
            {
                if (Eat('*')) v *= Power();
                else if (Eat('/'))
                {
                    var d = Power();
                    if (d == 0) throw new DivideByZeroException("division by zero");
                    v /= d;
                }
                else return v;
            }
        }

        private decimal Power()
        {
            var v = Unary();
            if (Eat('^'))
            {
                var e = Power();
                if (e != Math.Truncate(e) || e < 0 || e > 64) throw new FormatException("only whole powers from 0 to 64");
                var r = 1m;
                for (var k = 0; k < (int)e; k++) r *= v;
                return r;
            }
            return v;
        }

        private decimal Unary()
        {
            if (Eat('-')) return -Unary();
            if (Eat('+')) return Unary();
            var v = Atom();
            if (Eat('%')) v /= 100;
            return v;
        }

        private decimal Atom()
        {
            if (Eat('('))
            {
                var v = Expression();
                if (!Eat(')')) throw new FormatException("a ')' is missing");
                return v;
            }
            SkipSpace();
            var start = _i;
            while (_i < text.Length && (char.IsDigit(text[_i]) || text[_i] == '.')) _i++;
            if (start == _i) throw new FormatException(AtEnd ? "the expression ends too early" : $"unexpected '{text[_i]}'");
            return decimal.Parse(text[start.._i], CultureInfo.InvariantCulture);
        }
    }
}
