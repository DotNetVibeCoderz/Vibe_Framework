using Rustzor.Components;

var builder = RustzorHostBuilder.CreateDefault(args);
builder.RootComponents.Add<Siaga.App>("#app");
await builder.Build().RunAsync();
