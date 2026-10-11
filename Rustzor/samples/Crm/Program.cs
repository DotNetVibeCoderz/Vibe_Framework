using Rustzor.Components;

var builder = RustzorHostBuilder.CreateDefault(args);
builder.RootComponents.Add<Crm.App>("#app");
await builder.Build().RunAsync();
