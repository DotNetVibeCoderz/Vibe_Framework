using JSNet.Scripting;

namespace JSNet.Tests;

/// <summary>JSNet.Web, JSNet.EntityFramework, JSNet.Blazor and JSNet.Reactive used from JS.Net code.</summary>
public class PackageTests
{
    private static async Task<string> Run(string code, params Type[] anchors)
    {
        var refs = anchors.Select(t => t.Assembly).Distinct().ToList();
        var result = await JsNetScript.RunAsync(code, "pkg.jsn", new ScriptOptions { CaptureOutput = true, AdditionalReferences = refs, Timeout = TimeSpan.FromSeconds(60) });
        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + result.Exception + "\n--- C# ---\n" +
            string.Join("\n", result.GeneratedFiles.Select(f => f.Text)));
        return result.Output.Replace("\r\n", "\n").TrimEnd();
    }

    [Fact]
    public async Task QueryableOperatorsBuildExpressionTrees() => Assert.Equal("true [ 3, 4 ]", await Run("""
        const q = [1, 2, 3, 4].AsQueryable().where(x => x > 2);
        console.log(q.Expression.ToString().includes("Where"), q.toList());
        """));

    [Fact]
    public async Task EntityFrameworkQueriesTranslateToSql()
    {
        string output = await Run("""
            import Microsoft.EntityFrameworkCore;
            import Microsoft.Data.Sqlite;
            import JSNet.EntityFramework;

            class Todo { id: int = 0; title: string = ""; done: bool = false; }

            class AppDb extends DbContext {
                todos: DbSet<Todo>;
                constructor(private readonly connection: SqliteConnection) { super(); }
                protected override OnConfiguring(options: DbContextOptionsBuilder): void { options.UseSqlite(this.connection); }
            }

            const connection = new SqliteConnection("Data Source=:memory:");
            connection.Open();
            const db = new AppDb(connection);
            await db.ensureCreated();
            for (const title of ["write docs", "ship 0.2", "celebrate"]) db.todos.add(new Todo() { title: title, done: title == "write docs" });
            await db.save();

            const open = await db.todos.where(t => !t.done).orderBy(t => t.title).toListAsync();
            const sql = db.todos.where(t => t.done && t.title.Length > 3).ToQueryString();
            console.log(open.map(t => t.title), await db.todos.countAsync(), sql.includes("WHERE"));
            """, typeof(Microsoft.EntityFrameworkCore.DbContext), typeof(Microsoft.Data.Sqlite.SqliteConnection),
            typeof(Microsoft.EntityFrameworkCore.SqliteDbContextOptionsBuilderExtensions), typeof(JSNet.EntityFramework.JsEntityFrameworkExtensions),
            typeof(Microsoft.EntityFrameworkCore.Infrastructure.IInfrastructure<>));
        Assert.Equal("[ 'celebrate', 'ship 0.2' ] 3 true", output);
    }

    [Fact]
    public async Task ExpressStyleRoutesServeRequests()
    {
        string output = await Run("""
            import Microsoft.AspNetCore.Builder;
            import JSNet.Web;

            const app = WebApplication.Create();
            app.Urls.Add("http://127.0.0.1:5297");
            app.get("/hi/{name}", (name: string) => `hi ${name}`);
            app.get("/json", () => res.json({ ok: true }, 201));
            app.use(async (ctx, next) => { ctx.Response.Headers["x-powered-by"] = "JS.Net"; await next(); });
            await app.StartAsync();
            const hi = await fetch("http://127.0.0.1:5297/hi/ada");
            const json = await fetch("http://127.0.0.1:5297/json");
            console.log(await hi.text(), json.status, await json.text(), json.headers["x-powered-by"]);
            await app.StopAsync();
            """, typeof(Microsoft.AspNetCore.Builder.WebApplication), typeof(JSNet.Web.res));
        // ASP.NET Core also logs to the console; the script's own line is the one starting with "hi".
        Assert.Equal("hi ada 201 {\"ok\":true} JS.Net", output.Split('\n').Single(l => l.StartsWith("hi ", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task JsComponentsRenderWithBlazor()
    {
        string output = await Run("""
            import Microsoft.AspNetCore.Components;
            import Microsoft.AspNetCore.Components.Rendering;
            import Microsoft.AspNetCore.Components.Web;
            import Microsoft.Extensions.DependencyInjection;
            import Microsoft.Extensions.Logging.Abstractions;
            import JSNet.Blazor;

            class Greeting extends JsComponent {
                message: string = "loading";
                protected override async mounted(): Promise<void> { this.message = "Hello from JS.Net"; }
                protected override BuildRenderTree(builder: RenderTreeBuilder): void {
                    builder.OpenElement(0, "h1");
                    builder.AddContent(1, this.message);
                    builder.CloseElement();
                }
            }

            const services = new ServiceCollection().BuildServiceProvider();
            const renderer = new HtmlRenderer(services, NullLoggerFactory.Instance);
            const html = await renderer.Dispatcher.InvokeAsync(async () => (await renderer.RenderComponentAsync<Greeting>()).ToHtmlString());
            console.log(html);
            """, typeof(Microsoft.AspNetCore.Components.ComponentBase), typeof(Microsoft.AspNetCore.Components.Web.HtmlRenderer),
            typeof(Microsoft.Extensions.DependencyInjection.ServiceCollection), typeof(Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory),
            typeof(JSNet.Blazor.JsComponent));
        Assert.Equal("<h1>Hello from JS.Net</h1>", output);
    }

    [Fact]
    public async Task RxStyleObservables() => Assert.Equal("[ 20, 30, 40 ] [ 'a', 'b' ] 10", await Run("""
        import JSNet.Reactive;
        const xs = await rx.of(1, 2, 3, 4).map(x => x * 10).filter(x => x > 10).toArrayAsync();
        const seen = new Array<string>();
        const subject = rx.subject<string>();
        subject.subscribe(v => seen.push(v));
        subject.next("a");
        subject.next("b");
        subject.complete();
        const total = await rx.of(1, 2, 3, 4).scan(0, (acc, x) => acc + x).lastValue();
        console.log(xs, seen, total);
        """, typeof(JSNet.Reactive.rx), typeof(System.Reactive.Linq.Observable)));
}
