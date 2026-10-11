using Rustzor.Components;
var builder = RustzorHostBuilder.CreateDefault(args);
builder.RootComponents.Add<UiSmoke.App>("#app");
await builder.Build().RunAsync();
