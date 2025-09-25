using CropPlanner;
using StardewCropCalculatorLibrary;
using BlazorWorker.Core;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// ~~ Injection Types ~~
// Transient: new instance for each injection request, of which there's typically one per component. For us, that's one instance in the Home.razor component.
// Scoped: new instance per request. A SPA has one page request. So that's one instance for our entire app instance.
// Singleton: one instance for all requests. For us, that's the same thing as Scoped.

builder.Services.AddScoped(sp => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });

// Register 3P BlazorWorker library.
builder.Services.AddWorkerFactory();

// Register my JS system manager.
builder.Services.AddScoped<IHardwareInfo, HardwareInfo>();

// Register my worker manager.
builder.Services.AddScoped<ICalendarProcessor, CalendarProcessorService>();

// Register my scheduler, i.e. the core business logic.
builder.Services.AddTransient(sp =>
{
    var processor = sp.GetRequiredService<ICalendarProcessor>();
    return new GameStateCalendarFactory(processor);
});

await builder.Build().RunAsync();
