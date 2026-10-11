using Rustzor.Components;

var builder = RustzorHostBuilder.CreateDefault(args);
builder.RootComponents.Add<Dashboard.App>("#app");
await builder.Build().RunAsync();
