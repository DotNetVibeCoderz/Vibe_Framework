using Rustzor.Components;

var builder = RustzorHostBuilder.CreateDefault(args);
builder.RootComponents.Add<RustzorEmpty.App>("#app");
await builder.Build().RunAsync();
