using Rustzor.Components;

var builder = RustzorHostBuilder.CreateDefault(args);
builder.RootComponents.Add<Chat.App>("#app");
await builder.Build().RunAsync();
