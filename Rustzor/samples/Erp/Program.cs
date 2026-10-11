using Rustzor.Components;

var builder = RustzorHostBuilder.CreateDefault(args);
builder.RootComponents.Add<Erp.App>("#app");
await builder.Build().RunAsync();
