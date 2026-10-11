using Rustzor.Components;
var builder = RustzorHostBuilder.CreateDefault(args);
builder.RootComponents.Add<IntegrationsSmoke.App>("#app");
await builder.Build().RunAsync();
