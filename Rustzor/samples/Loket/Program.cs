using Rustzor.Components;

var builder = RustzorHostBuilder.CreateDefault(args);
builder.RootComponents.Add<Loket.App>("#app");
await builder.Build().RunAsync();
