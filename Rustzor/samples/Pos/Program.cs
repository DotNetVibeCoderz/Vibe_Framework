using Rustzor.Components;

var builder = RustzorHostBuilder.CreateDefault(args);
builder.RootComponents.Add<Pos.App>("#app");
await builder.Build().RunAsync();
