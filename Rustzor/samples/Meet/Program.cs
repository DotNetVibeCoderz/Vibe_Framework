using Rustzor.Components;

var builder = RustzorHostBuilder.CreateDefault(args);
builder.RootComponents.Add<Meet.App>("#app");
await builder.Build().RunAsync();
