using Rustzor.Interop;

namespace Rustzor.Charts.ECharts;

public readonly record struct EChartClick(string Series, string Name, double Value, int Index);

/// <summary>
/// Builders for common ECharts options. Anything they do not cover can be
/// passed as an anonymous object instead; ECharts' option format is the
/// documentation: https://echarts.apache.org/en/option.html
/// </summary>
public sealed class EChartOption
{
    private readonly JsonValue _root = JsonValue.NewObject();

    public string ToJson() => _root.ToJson();

    /// <summary>Sets any option path by JSON: <c>.Set("title", new { text = "Sales" })</c>.</summary>
    public EChartOption Set(string key, object? value)
    {
        _root[key] = JsonValue.Parse(Json.Serialize(value));
        return this;
    }

    private static JsonValue Array(IEnumerable<object?> items)
    {
        var a = JsonValue.NewArray();
        foreach (var i in items) a.Add(JsonValue.Parse(Json.Serialize(i)));
        return a;
    }

    private EChartOption Axes(IEnumerable<string> categories, bool horizontal)
    {
        var category = JsonValue.NewObject();
        category["type"] = JsonValue.From("category");
        category["data"] = Array(categories);
        var value = JsonValue.NewObject();
        value["type"] = JsonValue.From("value");
        _root[horizontal ? "yAxis" : "xAxis"] = category;
        _root[horizontal ? "xAxis" : "yAxis"] = value;
        _root["tooltip"] = JsonValue.Parse("{\"trigger\":\"axis\"}");
        _root["grid"] = JsonValue.Parse("{\"left\":48,\"right\":16,\"top\":40,\"bottom\":32}");
        return this;
    }

    private EChartOption Series(JsonValue series)
    {
        if (!_root.Has("series")) _root["series"] = JsonValue.NewArray();
        _root["series"].Add(series);
        if (_root["series"].Count > 1) _root["legend"] = JsonValue.Parse("{\"top\":0}");
        return this;
    }

    private static JsonValue SeriesOf(string type, string name, IEnumerable<double> data)
    {
        var s = JsonValue.NewObject();
        s["type"] = JsonValue.From(type);
        s["name"] = JsonValue.From(name);
        var d = JsonValue.NewArray();
        foreach (var v in data) d.Add(JsonValue.From(v));
        s["data"] = d;
        return s;
    }

    public static EChartOption Line(IEnumerable<string> categories) => new EChartOption().Axes(categories, false);

    public static EChartOption Bar(IEnumerable<string> categories, bool horizontal = false) => new EChartOption().Axes(categories, horizontal);

    public EChartOption AddLine(string name, IEnumerable<double> data, bool smooth = true, bool area = false)
    {
        var s = SeriesOf("line", name, data);
        s["smooth"] = JsonValue.From(smooth);
        if (area) s["areaStyle"] = JsonValue.Parse("{\"opacity\":0.15}");
        return Series(s);
    }

    public EChartOption AddBar(string name, IEnumerable<double> data, string? stack = null)
    {
        var s = SeriesOf("bar", name, data);
        if (stack is not null) s["stack"] = JsonValue.From(stack);
        s["itemStyle"] = JsonValue.Parse("{\"borderRadius\":[4,4,0,0]}");
        return Series(s);
    }

    /// <summary>A pie, or a ring when <paramref name="donut"/>.</summary>
    public static EChartOption Pie(string name, IEnumerable<(string Name, double Value)> items, bool donut = true)
    {
        var o = new EChartOption();
        var s = JsonValue.NewObject();
        s["type"] = JsonValue.From("pie");
        s["name"] = JsonValue.From(name);
        s["radius"] = JsonValue.Parse(donut ? "[\"45%\",\"72%\"]" : "\"72%\"");
        var d = JsonValue.NewArray();
        foreach (var (n, v) in items)
        {
            var item = JsonValue.NewObject();
            item["name"] = JsonValue.From(n);
            item["value"] = JsonValue.From(v);
            d.Add(item);
        }
        s["data"] = d;
        o._root["tooltip"] = JsonValue.Parse("{\"trigger\":\"item\"}");
        o._root["legend"] = JsonValue.Parse("{\"bottom\":0}");
        return o.Series(s);
    }

    /// <summary>A gauge for one value against a maximum.</summary>
    public static EChartOption Gauge(string name, double value, double max = 100, string unit = "")
    {
        var o = new EChartOption();
        var s = JsonValue.Parse("{\"type\":\"gauge\",\"progress\":{\"show\":true,\"width\":14},\"axisLine\":{\"lineStyle\":{\"width\":14}},\"detail\":{\"valueAnimation\":true,\"fontSize\":26,\"offsetCenter\":[0,\"60%\"]}}");
        s["name"] = JsonValue.From(name);
        s["max"] = JsonValue.From(max);
        s["detail"]["formatter"] = JsonValue.From("{value}" + unit);
        var d = JsonValue.NewArray();
        var item = JsonValue.NewObject();
        item["value"] = JsonValue.From(value);
        item["name"] = JsonValue.From(name);
        d.Add(item);
        s["data"] = d;
        return o.Series(s);
    }

    /// <summary>A heatmap over two category axes; cells are (x index, y index, value).</summary>
    public static EChartOption Heatmap(IEnumerable<string> xs, IEnumerable<string> ys, IEnumerable<(int X, int Y, double Value)> cells, double max)
    {
        var o = new EChartOption();
        o._root["xAxis"] = JsonValue.Parse("{\"type\":\"category\",\"splitArea\":{\"show\":true}}");
        o._root["xAxis"]["data"] = Array(xs);
        o._root["yAxis"] = JsonValue.Parse("{\"type\":\"category\",\"splitArea\":{\"show\":true}}");
        o._root["yAxis"]["data"] = Array(ys);
        o._root["visualMap"] = JsonValue.Parse("{\"min\":0,\"calculable\":true,\"orient\":\"horizontal\",\"left\":\"center\",\"bottom\":0}");
        o._root["visualMap"]["max"] = JsonValue.From(max);
        o._root["tooltip"] = JsonValue.Parse("{\"position\":\"top\"}");
        o._root["grid"] = JsonValue.Parse("{\"top\":16,\"bottom\":64,\"left\":64,\"right\":16}");
        var s = JsonValue.Parse("{\"type\":\"heatmap\",\"label\":{\"show\":false}}");
        var d = JsonValue.NewArray();
        foreach (var (x, y, v) in cells) d.Add(JsonValue.Parse($"[{x},{y},{JsonValue.From(v).ToJson()}]"));
        s["data"] = d;
        return o.Series(s);
    }

    public EChartOption Title(string text, string? subtext = null)
    {
        var t = JsonValue.NewObject();
        t["text"] = JsonValue.From(text);
        if (subtext is not null) t["subtext"] = JsonValue.From(subtext);
        _root["title"] = t;
        return this;
    }
}
