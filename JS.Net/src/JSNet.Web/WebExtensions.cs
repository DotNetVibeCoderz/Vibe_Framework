using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace JSNet.Web;

/// <summary>
/// Express-flavoured routing for ASP.NET Core minimal APIs:
/// <code>
/// app.get("/api/todos", () => store.all());
/// app.post("/api/todos", (todo: Todo) => store.add(todo));
/// app.use(async (ctx, next) => { console.log(ctx.Request.Path); await next(); });
/// app.listen(3000);
/// </code>
/// Handlers are ordinary minimal-API delegates: parameters bind from the route, query, body and DI.
/// </summary>
public static class JsWebExtensions
{
    private const string Dynamic = "Minimal API delegates are bound with reflection (use RequestDelegateGenerator-compatible code for AOT).";

    extension(IEndpointRouteBuilder app)
    {
        [RequiresUnreferencedCode(Dynamic), RequiresDynamicCode(Dynamic)]
        public RouteHandlerBuilder get([StringSyntax("Route")] string pattern, Delegate handler) => app.MapGet(pattern, handler);
        [RequiresUnreferencedCode(Dynamic), RequiresDynamicCode(Dynamic)]
        public RouteHandlerBuilder post([StringSyntax("Route")] string pattern, Delegate handler) => app.MapPost(pattern, handler);
        [RequiresUnreferencedCode(Dynamic), RequiresDynamicCode(Dynamic)]
        public RouteHandlerBuilder put([StringSyntax("Route")] string pattern, Delegate handler) => app.MapPut(pattern, handler);
        [RequiresUnreferencedCode(Dynamic), RequiresDynamicCode(Dynamic)]
        public RouteHandlerBuilder patch([StringSyntax("Route")] string pattern, Delegate handler) => app.MapPatch(pattern, handler);
        [RequiresUnreferencedCode(Dynamic), RequiresDynamicCode(Dynamic)]
        public RouteHandlerBuilder delete([StringSyntax("Route")] string pattern, Delegate handler) => app.MapDelete(pattern, handler);
        /// <summary>Groups routes under a prefix: <c>const api = app.route("/api");</c></summary>
        public RouteGroupBuilder route([StringSyntax("Route")] string prefix) => app.MapGroup(prefix);
    }

    extension(WebApplication app)
    {
        /// <summary>Express-style middleware: <c>app.use(async (ctx, next) =&gt; { ...; await next(); })</c>.</summary>
        public WebApplication use(Func<HttpContext, Func<Task>, Task> middleware)
        {
            app.Use((context, next) => middleware(context, () => next(context)));
            return app;
        }

        /// <summary>Serves files from wwwroot (or <paramref name="folder"/>) with default documents.</summary>
        public WebApplication serveStatic()
        {
            app.UseDefaultFiles();
            app.UseStaticFiles();
            return app;
        }

        /// <summary><c>app.listen(3000)</c> runs the server on http://localhost:3000.</summary>
        public void listen(int port) => app.Run($"http://localhost:{port}");

        public Task listenAsync(int port) => app.RunAsync($"http://localhost:{port}");
    }

    extension(HttpContext ctx)
    {
        public string path => ctx.Request.Path.Value ?? "/";
        public string method => ctx.Request.Method;
        public string? query(string name) => ctx.Request.Query.TryGetValue(name, out var v) ? v.ToString() : null;
        public string? header(string name) => ctx.Request.Headers.TryGetValue(name, out var v) ? v.ToString() : null;
    }
}

/// <summary>Result helpers: <c>return res.json(order, 201)</c>, <c>res.notFound()</c>, <c>res.text("ok")</c>.</summary>
public static class res
{
    public static IResult json(object? value, int status = 200) => Results.Json(value, statusCode: status);
    public static IResult text(string content, int status = 200) => Results.Text(content, statusCode: status);
    public static IResult html(string content) => Results.Content(content, "text/html");
    public static IResult status(int code) => Results.StatusCode(code);
    public static IResult ok(object? value = null) => value is null ? Results.Ok() : Results.Ok(value);
    public static IResult created(string location, object? value) => Results.Created(location, value);
    public static IResult notFound(object? value = null) => value is null ? Results.NotFound() : Results.NotFound(value);
    public static IResult badRequest(object? value = null) => value is null ? Results.BadRequest() : Results.BadRequest(value);
    public static IResult redirect(string url) => Results.Redirect(url);
}
