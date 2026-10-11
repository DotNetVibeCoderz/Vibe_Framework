namespace Rustzor.Charts.D3;

/// <summary>A labelled value for bar and pie charts.</summary>
public sealed record D3Datum(string Label, double Value);

/// <summary>A point in time for line charts.</summary>
public sealed record D3Point(DateTime Time, double Value);

/// <summary>A node of a force-directed graph; <see cref="Group"/> picks its colour.</summary>
public sealed record D3Node(string Id, string Label, int Group = 0, double Size = 6);

public sealed record D3Link(string Source, string Target, double Weight = 1);

/// <summary>A node of a hierarchy, for treemaps.</summary>
public sealed class D3Tree
{
    public D3Tree(string name, double value = 0)
    {
        Name = name;
        Value = value;
    }

    public string Name { get; }
    public double Value { get; }
    public List<D3Tree> Children { get; } = new();

    public D3Tree Add(D3Tree child)
    {
        Children.Add(child);
        return this;
    }
}
