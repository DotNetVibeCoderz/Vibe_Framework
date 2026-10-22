using Rustzor.Components;

var builder = RustzorHostBuilder.CreateDefault(args);
builder.RootComponents.Add<Bening.App>("#app");
await builder.Build().RunAsync();
