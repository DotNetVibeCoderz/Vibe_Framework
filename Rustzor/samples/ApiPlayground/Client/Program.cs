using Rustzor.Components;

var builder = RustzorHostBuilder.CreateDefault(args);
builder.RootComponents.Add<ApiPlayground.App>("#app");
await builder.Build().RunAsync();
