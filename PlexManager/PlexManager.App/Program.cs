using Microsoft.Extensions.FileProviders;
using PlexManager.App.Cli;
using PlexManager.App.Shell;
using PlexManager.App.Web;
using PlexManager.Core;
using PlexManager.Core.Jobs;
using PlexManager.Core.Operations;
using PlexManager.Core.Settings;

// One exe, two front ends over the same Core: a verb runs headless, no verb opens the window.
var command = CommandLine.Parse(args);
var paths = AppPaths.Initialize(command.DataFolder);

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

// A typo must not quietly open the window instead of running what was asked for.
if (command.Verb == "ui" && command.UnknownVerb is { } unknown)
{
    Console.Error.WriteLine($"Unknown verb \"{unknown}\".");
    Console.Error.WriteLine();
    Console.Error.WriteLine(CommandLine.Usage);
    return 1;
}

if (command.Verb != "ui" || command.Help)
{
    try
    {
        return await CliRunner.RunAsync(command, paths, cts.Token);
    }
    catch (OperationCanceledException)
    {
        Console.WriteLine("Cancelled.");
        return 2;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine(ex.Message);
        return 1;
    }
}

// ---- The window ----
using var instance = SingleInstance.Acquire(paths.Root);
if (!instance.IsFirst)
{
    if (instance.ExistingPort is { } running)
    {
        Console.WriteLine($"PlexManager is already running at http://127.0.0.1:{running}/ ; opening it.");
        AppWindow.OpenInBrowser($"http://127.0.0.1:{running}/");
        return 0;
    }

    Console.Error.WriteLine("PlexManager is already running for this data folder.");
    return 1;
}

var store = new SettingsStore(paths);
var runs = new RunStore(paths);
var jobs = new JobRunner(store, paths, runs);
var port = SingleInstance.PickPort(command.IntValue("port") ?? store.Current.Ui.Port);
if (store.Current.Ui.Port != port)
{
    store.Current.Ui.Port = port;
    store.Save();
}

SingleInstance.WritePort(paths.Root, port);

// WebRootPath is deliberately left unset: setting it makes the host create the folder, which would put an empty
// wwwroot next to the published exe. Static files are served from the provider chosen below instead.
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    ContentRootPath = AppContext.BaseDirectory,
});
builder.Logging.ClearProviders();
builder.WebHost.UseUrls($"http://127.0.0.1:{port}");

builder.Services.AddSingleton(paths);
builder.Services.AddSingleton(store);
builder.Services.AddSingleton(runs);
builder.Services.AddSingleton(jobs);

var app = builder.Build();

// The UI is embedded in the exe, so a published copy needs nothing beside it. A wwwroot folder on disk wins when
// it actually holds the UI, which is what makes editing a screen during development immediate. The test is for
// index.html rather than the folder, so an empty folder cannot shadow the embedded copy.
var wwwroot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
IFileProvider ui = File.Exists(Path.Combine(wwwroot, "index.html"))
    ? new PhysicalFileProvider(wwwroot)
    : new ManifestEmbeddedFileProvider(typeof(ApiEndpoints).Assembly, "wwwroot");

app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = ui });
app.UseStaticFiles(new StaticFileOptions { FileProvider = ui });
app.MapApi();
app.MapFallbackToFile("index.html", new StaticFileOptions { FileProvider = ui });

await app.StartAsync(cts.Token);

var url = $"http://127.0.0.1:{port}/";
if (command.Has("no-window"))
{
    Console.WriteLine($"PlexManager is serving its UI at {url}");
    Console.WriteLine("Press Ctrl+C to stop.");
    await app.WaitForShutdownAsync(cts.Token);
    return 0;
}

if (store.Current.Ui.PreferWebView && AppWindow.WebViewAvailable())
{
    AppWindow.Run(url, store, paths);
}
else
{
    Console.WriteLine($"WebView2 is not installed, so PlexManager opened {url} in your browser.");
    Console.WriteLine("Close this window to stop it.");
    AppWindow.OpenInBrowser(url);
    await app.WaitForShutdownAsync(cts.Token);
}

await app.StopAsync(CancellationToken.None);
return 0;
