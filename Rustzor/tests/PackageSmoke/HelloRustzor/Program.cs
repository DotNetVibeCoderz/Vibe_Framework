using Rustzor.Components;

var builder = RustzorHostBuilder.CreateDefault(args);
builder.RootComponents.Add<HelloRustzor.App>("#app");
await builder.Build().RunAsync();
