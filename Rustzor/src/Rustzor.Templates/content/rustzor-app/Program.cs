using Rustzor.Components;

var builder = RustzorHostBuilder.CreateDefault(args);
// Register services here: builder.Services.AddSingleton<MyService>();
builder.RootComponents.Add<RustzorApp.App>("#app");
await builder.Build().RunAsync();
