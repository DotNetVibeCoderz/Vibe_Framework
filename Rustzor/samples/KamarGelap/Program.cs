using Rustzor.Components;

var builder = RustzorHostBuilder.CreateDefault(args);
builder.RootComponents.Add<KamarGelap.App>("#app");
await builder.Build().RunAsync();
