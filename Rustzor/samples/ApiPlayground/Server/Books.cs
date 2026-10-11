using System.Collections.Concurrent;

namespace ApiPlayground.Server;

public sealed record Book(int Id, string Title, string Author, int Year, string Genre, double Rating);

/// <summary>An in-memory shelf of Indonesian literature, for the REST endpoints.</summary>
public sealed class Books
{
    private static readonly Book[] Shelf =
    {
        new(1, "Bumi Manusia", "Pramoedya Ananta Toer", 1980, "Historical", 4.6),
        new(2, "Laskar Pelangi", "Andrea Hirata", 2005, "Coming of age", 4.4),
        new(3, "Cantik Itu Luka", "Eka Kurniawan", 2002, "Magical realism", 4.3),
        new(4, "Ronggeng Dukuh Paruk", "Ahmad Tohari", 1982, "Literary", 4.3),
        new(5, "Saman", "Ayu Utami", 1998, "Literary", 4.0),
        new(6, "Pulang", "Leila S. Chudori", 2012, "Historical", 4.4),
        new(7, "Ca-Bau-Kan", "Remy Sylado", 1999, "Historical", 4.1),
        new(8, "Negeri 5 Menara", "Ahmad Fuadi", 2009, "Coming of age", 4.2),
        new(9, "Supernova", "Dee Lestari", 2001, "Science fiction", 4.1),
        new(10, "Perahu Kertas", "Dee Lestari", 2009, "Romance", 4.2),
        new(11, "Gadis Kretek", "Ratih Kumala", 2012, "Historical", 4.3),
        new(12, "Atheis", "Achdiat K. Mihardja", 1949, "Literary", 4.0),
    };

    private readonly ConcurrentDictionary<int, Book> _books = new();
    private int _nextId;

    public Books() => Reset();

    public void Reset()
    {
        _books.Clear();
        foreach (var b in Shelf) _books[b.Id] = b;
        _nextId = Shelf.Length;
    }

    public IEnumerable<Book> Find(string? q, string? sort)
    {
        var list = _books.Values.Where(b => string.IsNullOrWhiteSpace(q)
            || b.Title.Contains(q, StringComparison.OrdinalIgnoreCase)
            || b.Author.Contains(q, StringComparison.OrdinalIgnoreCase)
            || b.Genre.Contains(q, StringComparison.OrdinalIgnoreCase));
        return sort switch
        {
            "year" => list.OrderBy(b => b.Year),
            "rating" => list.OrderByDescending(b => b.Rating),
            "title" => list.OrderBy(b => b.Title),
            _ => list.OrderBy(b => b.Id),
        };
    }

    public Book? Get(int id) => _books.TryGetValue(id, out var b) ? b : null;

    public Book Add(Book input)
    {
        var book = input with { Id = Interlocked.Increment(ref _nextId) };
        _books[book.Id] = book;
        return book;
    }

    public Book? Update(int id, Book input)
    {
        if (!_books.ContainsKey(id)) return null;
        var book = input with { Id = id };
        _books[id] = book;
        return book;
    }

    public bool Delete(int id) => _books.TryRemove(id, out _);
}
