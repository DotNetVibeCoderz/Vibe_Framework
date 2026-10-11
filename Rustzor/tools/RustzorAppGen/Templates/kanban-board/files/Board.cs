using Rustzor.Data;

namespace {{Namespace}};

public sealed class TaskCard
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Notes { get; set; } = "";
    public string Column { get; set; } = "backlog";
    public string Priority { get; set; } = "normal";
    public string Tag { get; set; } = "";
    public double Order { get; set; }
    public long Created { get; set; }
}

public sealed record BoardColumn(string Id, string Title, int Limit);

/// <summary>Cards live in one IndexedDB store; columns are fixed.</summary>
public sealed class Board
{
    public static readonly BoardColumn[] Columns =
    {
        new("backlog", "Backlog", 0),
        new("doing", "In progress", 3),
        new("review", "Review", 2),
        new("done", "Done", 0),
    };

    public static readonly string[] Priorities = { "low", "normal", "high", "urgent" };

    private IndexedDb? _db;
    private ObjectStore<TaskCard>? _store;
    public List<TaskCard> Cards { get; } = new();

    public async Task LoadAsync()
    {
        _db = await IndexedDb.OpenAsync("kanban", 1, new StoreSchema("cards", "id", Indexes: new IndexSchema("byColumn", "column")));
        _store = _db.Store<TaskCard>("cards");
        Cards.Clear();
        Cards.AddRange(await _store.AllAsync());
        if (Cards.Count == 0)
        {
            foreach (var c in Samples()) { Cards.Add(c); }
            await _store.PutManyAsync(Cards);
        }
    }

    public IEnumerable<TaskCard> In(string column) => Cards.Where(c => c.Column == column).OrderBy(c => c.Order);

    public async Task SaveAsync(TaskCard card)
    {
        if (card.Id.Length == 0)
        {
            card.Id = Guid.NewGuid().ToString("N");
            card.Created = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            card.Order = NextOrder(card.Column);
            Cards.Add(card);
        }
        if (_store is not null) await _store.PutAsync(card);
    }

    public async Task MoveAsync(TaskCard card, string column)
    {
        if (card.Column == column) return;
        card.Column = column;
        card.Order = NextOrder(column);
        if (_store is not null) await _store.PutAsync(card);
    }

    public async Task DeleteAsync(TaskCard card)
    {
        Cards.Remove(card);
        if (_store is not null) await _store.DeleteAsync(card.Id);
    }

    private double NextOrder(string column) => In(column).Select(c => c.Order).DefaultIfEmpty(0).Max() + 1;

    private static IEnumerable<TaskCard> Samples()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var i = 0;
        TaskCard Make(string title, string column, string priority, string tag, string notes = "") =>
            new() { Id = $"sample-{++i}", Title = title, Column = column, Priority = priority, Tag = tag, Notes = notes, Order = i, Created = now - i * 3_600_000 };
        yield return Make("Write onboarding checklist", "backlog", "normal", "docs");
        yield return Make("Audit image sizes on landing page", "backlog", "low", "perf");
        yield return Make("Export invoices as CSV", "backlog", "high", "feature", "Finance needs this before month end.");
        yield return Make("Fix login redirect loop", "doing", "urgent", "bug", "Happens after the session expires on Safari.");
        yield return Make("Dark mode for settings page", "doing", "normal", "ui");
        yield return Make("Review pricing copy", "review", "normal", "docs");
        yield return Make("Upgrade to Rustzor 0.1", "done", "high", "chore");
        yield return Make("Set up error reporting", "done", "normal", "chore");
    }
}
