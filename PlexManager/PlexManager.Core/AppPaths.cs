namespace PlexManager.Core;

/// <summary>
/// The one place that knows where PlexManager keeps its data. By default that is %APPDATA%\PlexManager; a
/// <c>portable.flag</c> file next to the exe, or <c>--data &lt;folder&gt;</c>, puts everything in the exe's own folder instead.
/// Replaces the old "walk up from the working directory looking for plexmanager.config.json" search, which an installed exe cannot rely on.
/// </summary>
public sealed class AppPaths
{
    private AppPaths(string root)
    {
        Root = root;
    }

    public static AppPaths Current { get; private set; } = Resolve(null);

    public string Root { get; }

    public string SettingsFile => Path.Combine(Root, "settings.json");

    public string Reports => Path.Combine(Root, "Reports");

    public string Backups => Path.Combine(Root, "Backups");

    public string Runs => Path.Combine(Root, "Runs");

    public string Caches => Path.Combine(Root, "Caches");

    public string Logs => Path.Combine(Root, "Logs");

    /// <summary>Snapshots of the live Trakt lists taken right after a cleanup, as the old TraktBackup\Current folder did.</summary>
    public string CurrentLists => Path.Combine(Root, "Current");

    public string Cache(string fileName) => Path.Combine(Caches, fileName);

    /// <summary>Points every later call at <paramref name="dataOverride"/> (<c>--data</c>) or back at the default.</summary>
    public static AppPaths Initialize(string? dataOverride)
    {
        Current = Resolve(dataOverride);
        Current.EnsureCreated();
        return Current;
    }

    public void EnsureCreated()
    {
        foreach (var dir in new[] { Root, Reports, Backups, Runs, Caches, Logs })
        {
            Directory.CreateDirectory(dir);
        }
    }

    private static AppPaths Resolve(string? dataOverride)
    {
        if (!string.IsNullOrWhiteSpace(dataOverride))
        {
            return new AppPaths(Path.GetFullPath(dataOverride));
        }

        var beside = AppContext.BaseDirectory;
        if (File.Exists(Path.Combine(beside, "portable.flag")))
        {
            return new AppPaths(Path.GetFullPath(beside));
        }

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return new AppPaths(Path.Combine(appData, "PlexManager"));
    }
}
