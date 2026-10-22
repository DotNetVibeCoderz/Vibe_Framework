using Rustzor.Components;

var builder = RustzorHostBuilder.CreateDefault(args);
builder.RootComponents.Add<Tilik.App>("#app");
await builder.Build().RunAsync();
