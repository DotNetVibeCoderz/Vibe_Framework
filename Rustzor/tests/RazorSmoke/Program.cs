using Rustzor.Components;
using RazorSmoke;

var builder = RustzorHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.Services.AddSingleton<Counter>();
await builder.Build().RunAsync();

namespace RazorSmoke
{
    public class Counter { public int Total; }
}
