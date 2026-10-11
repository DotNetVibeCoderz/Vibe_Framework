using Avalonia.Data.Converters;
using RustzorAppGen.Services;

namespace RustzorAppGen.Views;

public static class PathConverters
{
    public static readonly IValueConverter FileName = new FuncValueConverter<string?, string>(p => p is null ? "" : Path.GetFileName(p.TrimEnd('\\', '/')));
    public static readonly IValueConverter Parent = new FuncValueConverter<string?, string>(p => p is null ? "" : Path.GetDirectoryName(p.TrimEnd('\\', '/')) ?? "");
    public static readonly IValueConverter IsZero = new FuncValueConverter<int, bool>(n => n == 0);
    public static readonly IValueConverter NonZero = new FuncValueConverter<int, bool>(n => n != 0);
    public static readonly IValueConverter ProblemLocation = new FuncValueConverter<BuildProblem?, string>(p => p is null ? "" : $"{Path.GetFileName(p.File)}:{p.Line}");
}
