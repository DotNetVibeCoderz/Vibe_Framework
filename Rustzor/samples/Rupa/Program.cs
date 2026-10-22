using Rustzor.Components;

var builder = RustzorHostBuilder.CreateDefault(args);
builder.RootComponents.Add<Rupa.App>("#app");
await builder.Build().RunAsync();
