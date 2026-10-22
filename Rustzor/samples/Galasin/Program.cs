using Rustzor.Components;

var builder = RustzorHostBuilder.CreateDefault(args);
builder.RootComponents.Add<Galasin.App>("#app");
await builder.Build().RunAsync();
