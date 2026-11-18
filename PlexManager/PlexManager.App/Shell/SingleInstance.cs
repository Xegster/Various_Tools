using System.Net.Sockets;

namespace PlexManager.App.Shell;

/// <summary>
/// Keeps one window per data folder (phase 5). The lock is a named mutex derived from the data folder, so a
/// portable copy and the installed one can both run, and the port the first instance chose is left in a file
/// beside the settings so a second launch can point the browser at the window that is already up.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private readonly Mutex? _mutex;

    private SingleInstance(Mutex? mutex, bool owned, int? existingPort)
    {
        _mutex = mutex;
        IsFirst = owned;
        ExistingPort = existingPort;
    }

    public bool IsFirst { get; }

    /// <summary>The port the instance that is already running is serving on, when there is one.</summary>
    public int? ExistingPort { get; }

    public static SingleInstance Acquire(string dataFolder)
    {
        var name = "Local\\PlexManager-" + Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(dataFolder).ToLowerInvariant())))[..16];

        var mutex = new Mutex(initiallyOwned: false, name, out _);
        bool owned;
        try
        {
            owned = mutex.WaitOne(TimeSpan.Zero);
        }
        catch (AbandonedMutexException)
        {
            // The previous instance died without releasing it; this one owns it now.
            owned = true;
        }

        return new SingleInstance(mutex, owned, owned ? null : ReadPort(dataFolder));
    }

    /// <summary>Remembers the port this instance is serving on.</summary>
    public static void WritePort(string dataFolder, int port)
    {
        try
        {
            File.WriteAllText(Path.Combine(dataFolder, "ui-port.txt"), port.ToString());
        }
        catch (IOException)
        {
            // Not being able to record the port only costs the "focus the existing window" trick.
        }
    }

    /// <summary>A free loopback port, or the remembered one when it is still free.</summary>
    public static int PickPort(int preferred)
    {
        if (preferred > 0 && IsFree(preferred))
        {
            return preferred;
        }

        using var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static bool IsFree(int port)
    {
        try
        {
            using var listener = new TcpListener(System.Net.IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private static int? ReadPort(string dataFolder)
    {
        var file = Path.Combine(dataFolder, "ui-port.txt");
        return File.Exists(file) && int.TryParse(File.ReadAllText(file).Trim(), out var port) ? port : null;
    }

    public void Dispose()
    {
        if (_mutex == null)
        {
            return;
        }

        if (IsFirst)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // Already released.
            }
        }

        _mutex.Dispose();
    }
}
