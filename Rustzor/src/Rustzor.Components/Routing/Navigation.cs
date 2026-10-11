using Rustzor.Interop;

namespace Microsoft.AspNetCore.Components
{
    /// <summary>The browser's address: read it, change it, hear when it changes.</summary>
    public class NavigationManager
    {
        internal static readonly NavigationManager Instance = new();

        private bool _initialized;

        public string BaseUri { get; private set; } = "/";

        public string Uri { get; private set; } = "/";

        public event EventHandler<Routing.LocationChangedEventArgs>? LocationChanged;

        internal void Initialize()
        {
            if (_initialized) return;
            _initialized = true;
            if (!Js.IsBrowser) return;
            var info = Js.Call("nav.init");
            BaseUri = info["base"].AsString();
            Uri = info["href"].AsString();
        }

        /// <summary>Goes to <paramref name="uri"/>: within the app without a reload, or with one when <paramref name="forceLoad"/>.</summary>
        public void NavigateTo(string uri, bool forceLoad = false, bool replace = false)
        {
            var absolute = ToAbsoluteUri(uri);
            if (forceLoad || !absolute.StartsWith(BaseUri, StringComparison.Ordinal))
            {
                Js.Send("nav.load", w => w.BeginObject().Prop("url", absolute).Prop("replace", replace).EndObject());
                return;
            }
            Js.Send("nav.push", w => w.BeginObject().Prop("url", absolute).Prop("replace", replace).EndObject());
            SetUri(absolute, false);
        }

        public void NavigateTo(string uri, NavigationOptions options) => NavigateTo(uri, options.ForceLoad, options.ReplaceHistoryEntry);

        public void Refresh(bool forceReload = false) => NavigateTo(Uri, forceReload, true);

        public string ToAbsoluteUri(string relativeUri)
        {
            if (relativeUri.StartsWith("http://") || relativeUri.StartsWith("https://")) return relativeUri;
            if (relativeUri.StartsWith("/"))
            {
                // Rooted: relative to the origin, not the base path.
                var schemeEnd = BaseUri.IndexOf("://");
                var originEnd = schemeEnd < 0 ? -1 : BaseUri.IndexOf('/', schemeEnd + 3);
                return originEnd < 0 ? relativeUri : BaseUri.Substring(0, originEnd) + relativeUri;
            }
            return BaseUri + relativeUri;
        }

        /// <summary>The path relative to the base, without query or fragment: <c>"customers/42"</c>.</summary>
        public string ToBaseRelativePath(string uri)
        {
            if (uri.StartsWith(BaseUri, StringComparison.Ordinal)) return uri.Substring(BaseUri.Length);
            var trimmed = BaseUri.EndsWith("/") ? BaseUri.Substring(0, BaseUri.Length - 1) : BaseUri;
            if (uri == trimmed) return "";
            return uri;
        }

        internal void OnBrowserNavigated(string uri, bool intercepted) => SetUri(uri, intercepted);

        private void SetUri(string uri, bool intercepted)
        {
            Uri = uri;
            LocationChanged?.Invoke(this, new Routing.LocationChangedEventArgs(uri, intercepted));
        }
    }

    public readonly struct NavigationOptions
    {
        public bool ForceLoad { get; init; }
        public bool ReplaceHistoryEntry { get; init; }
    }
}

namespace Microsoft.AspNetCore.Components.Routing
{
    public class LocationChangedEventArgs : EventArgs
    {
        public LocationChangedEventArgs(string location, bool isNavigationIntercepted)
        {
            Location = location;
            IsNavigationIntercepted = isNavigationIntercepted;
        }

        public string Location { get; }
        public bool IsNavigationIntercepted { get; }
    }

    public enum NavLinkMatch
    {
        Prefix,
        All,
    }
}
