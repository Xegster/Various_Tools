using System.Diagnostics;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using PlexManager.Core;
using PlexManager.Core.Settings;

namespace PlexManager.App.Shell;

/// <summary>
/// The window: a WebView2 control filling a plain form, pointed at the loopback address Kestrel is serving.
/// WebView2 ships with Windows 11 and updated Windows 10; when it is missing the default browser is opened instead,
/// so the app still works with nothing installed.
/// </summary>
public static class AppWindow
{
    /// <summary>Whether a WebView2 runtime is present.</summary>
    public static bool WebViewAvailable()
    {
        try
        {
            CoreWebView2Environment.GetAvailableBrowserVersionString();
            return true;
        }
        catch (Exception ex) when (ex is WebView2RuntimeNotFoundException or DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    /// <summary>
    /// Shows the window and returns when it is closed. Must be called on the main thread; blocks in the WinForms
    /// message loop, which is why the web host runs in the background.
    /// </summary>
    public static void Run(string url, SettingsStore store, AppPaths paths)
    {
        ApplicationConfiguration.Initialize();

        var ui = store.Current.Ui;
        using var form = new Form
        {
            Text = "PlexManager",
            Width = Math.Max(900, ui.WindowWidth),
            Height = Math.Max(600, ui.WindowHeight),
            StartPosition = FormStartPosition.CenterScreen,
            MinimumSize = new Size(900, 600),
        };

        var icon = TryLoadIcon();
        if (icon != null)
        {
            form.Icon = icon;
        }

        var view = new WebView2 { Dock = DockStyle.Fill };
        form.Controls.Add(view);

        form.FormClosing += (_, _) =>
        {
            ui.WindowWidth = form.Width;
            ui.WindowHeight = form.Height;
            store.Save();
        };

        form.Shown += async (_, _) =>
        {
            try
            {
                // Keep the browser profile in the data folder, not beside the exe, so a read-only folder still works.
                var environment = await CoreWebView2Environment.CreateAsync(
                    userDataFolder: Path.Combine(paths.Root, "WebView2"));
                await view.EnsureCoreWebView2Async(environment);

                view.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                view.CoreWebView2.Settings.IsStatusBarEnabled = false;
                view.CoreWebView2.Settings.AreDevToolsEnabled = true;

                // Links to trakt.tv, plex.tv and IMDb belong in the real browser, not in this window.
                view.CoreWebView2.NewWindowRequested += (_, e) =>
                {
                    e.Handled = true;
                    OpenInBrowser(e.Uri);
                };

                view.CoreWebView2.Navigate(url);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"The built-in window could not start ({ex.Message}).\n\nOpening {url} in your browser instead.",
                    "PlexManager",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                OpenInBrowser(url);
                form.Close();
            }
        };

        Application.Run(form);
        icon?.Dispose();
    }

    public static void OpenInBrowser(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            Console.Error.WriteLine($"Could not open {url}: {ex.Message}");
        }
    }

    private static Icon? TryLoadIcon()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "plexmanager.ico");
        if (File.Exists(path))
        {
            return new Icon(path);
        }

        try
        {
            return Icon.ExtractAssociatedIcon(Environment.ProcessPath ?? "");
        }
        catch (Exception ex) when (ex is ArgumentException or IOException)
        {
            return null;
        }
    }
}
