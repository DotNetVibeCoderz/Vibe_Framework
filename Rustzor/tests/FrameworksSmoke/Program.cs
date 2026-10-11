using Rustzor.Components;
var builder = RustzorHostBuilder.CreateDefault(args);
builder.RootComponents.Add<FrameworksSmoke.App>("#app");
await builder.Build().RunAsync();
