using System.Globalization;
using System.Reflection;
using Microsoft.AspNetCore.Components.Rendering;
using Rustzor.Interop;

namespace Microsoft.AspNetCore.Components
{
    /// <summary>The page a URL resolved to, and the values its template captured.</summary>
    public sealed class RouteData
    {
        public RouteData(Type pageType, IReadOnlyDictionary<string, object?> routeValues)
        {
            PageType = pageType;
            RouteValues = routeValues;
        }

        public Type PageType { get; }
        public IReadOnlyDictionary<string, object?> RouteValues { get; }
        public string? Template { get; init; }
    }

    /// <summary>Renders a page inside its layout.</summary>
    public class RouteView : ComponentBase
    {
        [Parameter] public RouteData RouteData { get; set; } = null!;
        [Parameter] public Type? DefaultLayout { get; set; }

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            var layout = LayoutOf(RouteData.PageType) ?? DefaultLayout;
            RenderFragment page = RenderPage;
            if (layout is null)
            {
                page(builder);
                return;
            }
            // Layouts nest: a layout may itself have a [Layout].
            var chain = new List<Type>();
            for (var l = layout; l is not null && !chain.Contains(l); l = LayoutOf(l)) chain.Add(l);
            RenderFragment body = page;
            for (var i = 0; i < chain.Count; i++)
            {
                var layoutType = chain[i];
                var inner = body;
                body = b =>
                {
                    b.OpenComponent(0, layoutType);
                    b.AddComponentParameter(1, "Body", inner);
                    b.CloseComponent();
                };
            }
            body(builder);
        }

        private void RenderPage(RenderTreeBuilder b)
        {
            b.OpenComponent(0, RouteData.PageType);
            foreach (var kv in RouteData.RouteValues) b.AddComponentParameter(1, kv.Key, kv.Value);
            b.CloseComponent();
        }

        internal static Type? LayoutOf(Type type)
        {
            foreach (var a in type.GetCustomAttributes(typeof(LayoutAttribute), true))
                if (a is LayoutAttribute la) return la.LayoutType;
            return null;
        }
    }

    /// <summary>Renders an arbitrary fragment inside a layout.</summary>
    public class LayoutView : ComponentBase
    {
        [Parameter] public Type? Layout { get; set; }
        [Parameter] public RenderFragment? ChildContent { get; set; }

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            if (Layout is null)
            {
                builder.AddContent(0, ChildContent);
                return;
            }
            builder.OpenComponent(1, Layout);
            builder.AddComponentParameter(2, "Body", ChildContent);
            builder.CloseComponent();
        }
    }

    /// <summary>Sets the document's title while it is rendered.</summary>
    public class PageTitle : ComponentBase
    {
        [Parameter] public RenderFragment? ChildContent { get; set; }

        protected override void OnParametersSet()
        {
            if (ChildContent is null || !Js.IsBrowser) return;
            var b = new RenderTreeBuilder();
            ChildContent(b);
            var sb = new System.Text.StringBuilder();
            Collect(b.Roots, sb);
            Js.Send("doc.title", w => w.BeginObject().Prop("title", sb.ToString()).EndObject());
        }

        private static void Collect(List<VNode> nodes, System.Text.StringBuilder sb)
        {
            foreach (var n in nodes)
            {
                if (n is VText t) sb.Append(t.Text);
                else if (n is VRegion r) Collect(r.Children, sb);
                else if (n is VElement e) Collect(e.Children, sb);
            }
        }
    }
}

namespace Microsoft.AspNetCore.Components.Routing
{
    /// <summary>
    /// Finds the <c>[Route]</c> pages in an assembly and renders the one the
    /// address matches, or <see cref="NotFound"/>.
    /// </summary>
    public class Router : ComponentBase, IDisposable
    {
        private RouteTable? _table;
        private bool _subscribed;

        [Inject] private NavigationManager Navigation { get; set; } = null!;

        [Parameter] public Assembly AppAssembly { get; set; } = null!;
        [Parameter] public IEnumerable<Assembly>? AdditionalAssemblies { get; set; }
        [Parameter] public RenderFragment<RouteData>? Found { get; set; }
        [Parameter] public RenderFragment? NotFound { get; set; }
        [Parameter] public RenderFragment? Navigating { get; set; }
        [Parameter] public EventCallback<NavigationContext> OnNavigateAsync { get; set; }
        [Parameter] public Type? NotFoundPage { get; set; }

        protected override void OnParametersSet()
        {
            if (_table is null)
            {
                var assemblies = new List<Assembly> { AppAssembly };
                if (AdditionalAssemblies is not null) assemblies.AddRange(AdditionalAssemblies);
                _table = RouteTable.Build(assemblies);
            }
            if (!_subscribed)
            {
                _subscribed = true;
                Navigation.LocationChanged += OnLocationChanged;
            }
        }

        private void OnLocationChanged(object? sender, LocationChangedEventArgs e)
        {
            StateHasChanged();
            if (Js.IsBrowser) Js.Send("nav.scrollTop");
        }

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            var path = Navigation.ToBaseRelativePath(Navigation.Uri);
            var cut = path.IndexOfAny(new[] { '?', '#' });
            if (cut >= 0) path = path.Substring(0, cut);
            var data = _table!.Match(path);
            if (data is not null && Found is not null)
            {
                builder.AddContent(0, Found(data));
                return;
            }
            if (NotFoundPage is not null)
            {
                builder.OpenComponent(1, NotFoundPage);
                builder.CloseComponent();
                return;
            }
            builder.AddContent(2, NotFound);
        }

        public void Dispose()
        {
            if (_subscribed) Navigation.LocationChanged -= OnLocationChanged;
        }
    }

    public sealed class NavigationContext
    {
        public NavigationContext(string path) => Path = path;

        public string Path { get; }
    }

    internal sealed class RouteTable
    {
        private readonly List<RouteEntry> _routes = new();

        public static RouteTable Build(IEnumerable<Assembly> assemblies)
        {
            var table = new RouteTable();
            foreach (var assembly in assemblies)
            {
                foreach (var type in assembly.GetTypes())
                {
                    foreach (var a in type.GetCustomAttributes(typeof(RouteAttribute), false))
                    {
                        if (a is RouteAttribute ra) table._routes.Add(RouteEntry.Parse(ra.Template, type));
                    }
                }
            }
            // Most specific first: more literal segments, then fewer optional ones.
            table._routes.Sort((x, y) => y.Score.CompareTo(x.Score));
            return table;
        }

        public RouteData? Match(string path)
        {
            var segments = Split(path);
            foreach (var route in _routes)
            {
                var values = route.TryMatch(segments);
                if (values is not null) return new RouteData(route.PageType, values) { Template = route.Template };
            }
            return null;
        }

        internal static string[] Split(string path)
        {
            var parts = new List<string>();
            foreach (var p in path.Split('/'))
                if (p.Length > 0) parts.Add(System.Uri.UnescapeDataString(p));
            return parts.ToArray();
        }
    }

    internal sealed class RouteEntry
    {
        private sealed class Segment
        {
            public string Literal = "";
            public string? Parameter;
            public string? Constraint;
            public bool Optional;
            public bool CatchAll;
        }

        private readonly List<Segment> _segments = new();

        public string Template { get; private set; } = "";
        public Type PageType { get; private set; } = null!;
        public int Score { get; private set; }

        public static RouteEntry Parse(string template, Type pageType)
        {
            var entry = new RouteEntry { Template = template, PageType = pageType };
            foreach (var raw in template.Split('/'))
            {
                if (raw.Length == 0) continue;
                var s = new Segment();
                if (raw.StartsWith("{") && raw.EndsWith("}"))
                {
                    var inner = raw.Substring(1, raw.Length - 2);
                    if (inner.StartsWith("*") || inner.StartsWith("**"))
                    {
                        s.CatchAll = true;
                        inner = inner.TrimStart('*');
                    }
                    if (inner.EndsWith("?"))
                    {
                        s.Optional = true;
                        inner = inner.Substring(0, inner.Length - 1);
                    }
                    var colon = inner.IndexOf(':');
                    if (colon >= 0)
                    {
                        s.Constraint = inner.Substring(colon + 1);
                        inner = inner.Substring(0, colon);
                    }
                    s.Parameter = inner;
                    entry.Score += s.CatchAll ? 0 : s.Optional ? 1 : 2 + (s.Constraint is null ? 0 : 1);
                }
                else
                {
                    s.Literal = raw;
                    entry.Score += 10;
                }
                entry._segments.Add(s);
            }
            entry.Score = entry.Score * 100 + entry._segments.Count;
            return entry;
        }

        public Dictionary<string, object?>? TryMatch(string[] path)
        {
            var values = new Dictionary<string, object?>();
            var i = 0;
            foreach (var s in _segments)
            {
                if (s.CatchAll)
                {
                    values[s.Parameter!] = string.Join("/", path, i, path.Length - i);
                    return values;
                }
                if (i >= path.Length)
                {
                    if (s.Parameter is not null && s.Optional) continue;
                    return null;
                }
                if (s.Parameter is null)
                {
                    if (!string.Equals(s.Literal, path[i], StringComparison.OrdinalIgnoreCase)) return null;
                }
                else
                {
                    var converted = Convert(path[i], s.Constraint);
                    if (converted is null) return null;
                    values[s.Parameter] = converted;
                }
                i++;
            }
            return i == path.Length ? values : null;
        }

        private static object? Convert(string value, string? constraint)
        {
            switch (constraint)
            {
                case null:
                    return value;
                case "int":
                    return int.TryParse(value, out var i) ? i : null;
                case "long":
                    return long.TryParse(value, out var l) ? l : null;
                case "double":
                case "float":
                case "decimal":
                    return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
                case "bool":
                    return bool.TryParse(value, out var b) ? b : null;
                default:
                    return value;
            }
        }
    }

    /// <summary>An anchor that gets an "active" class when its href is the current page.</summary>
    public class NavLink : ComponentBase, IDisposable
    {
        private bool _subscribed;
        private bool _isActive;

        [Inject] private NavigationManager Navigation { get; set; } = null!;

        [Parameter] public string ActiveClass { get; set; } = "active";
        [Parameter] public NavLinkMatch Match { get; set; }
        [Parameter] public RenderFragment? ChildContent { get; set; }
        [Parameter(CaptureUnmatchedValues = true)] public IReadOnlyDictionary<string, object>? AdditionalAttributes { get; set; }

        protected override void OnParametersSet()
        {
            if (!_subscribed)
            {
                _subscribed = true;
                Navigation.LocationChanged += OnLocationChanged;
            }
            _isActive = ComputeActive();
        }

        private void OnLocationChanged(object? sender, LocationChangedEventArgs e)
        {
            var active = ComputeActive();
            if (active == _isActive) return;
            _isActive = active;
            StateHasChanged();
        }

        private bool ComputeActive()
        {
            string? href = null;
            if (AdditionalAttributes is not null && AdditionalAttributes.TryGetValue("href", out var h)) href = h?.ToString();
            if (href is null) return false;
            var target = Navigation.ToBaseRelativePath(Navigation.ToAbsoluteUri(href)).TrimEnd('/');
            var current = Navigation.ToBaseRelativePath(Navigation.Uri);
            var cut = current.IndexOfAny(new[] { '?', '#' });
            if (cut >= 0) current = current.Substring(0, cut);
            current = current.TrimEnd('/');
            if (Match == NavLinkMatch.All || target.Length == 0) return string.Equals(current, target, StringComparison.OrdinalIgnoreCase);
            return current.Equals(target, StringComparison.OrdinalIgnoreCase)
                || current.StartsWith(target + "/", StringComparison.OrdinalIgnoreCase);
        }

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenElement(0, "a");
            string? cls = null;
            if (AdditionalAttributes is not null)
            {
                foreach (var kv in AdditionalAttributes)
                {
                    if (kv.Key == "class") cls = kv.Value?.ToString();
                    else builder.AddAttribute(1, kv.Key, kv.Value);
                }
            }
            var combined = _isActive ? (cls is null ? ActiveClass : cls + " " + ActiveClass) : cls;
            if (combined is not null) builder.AddAttribute(2, "class", combined);
            builder.AddContent(3, ChildContent);
            builder.CloseElement();
        }

        public void Dispose()
        {
            if (_subscribed) Navigation.LocationChanged -= OnLocationChanged;
        }
    }
}
