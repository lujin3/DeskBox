using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32;

namespace DeskBox.Services;

/// <summary>
/// Reads and flips the Windows manual system-proxy switch — the same
/// "Manual proxy setup" toggle the Windows Settings app drives. Backed by the
/// per-user WinINet settings in the registry; no elevation required.
/// </summary>
/// <remarks>
/// <para>
/// Toggle-only by design: it writes <c>ProxyEnable</c> and never rewrites
/// <c>ProxyServer</c>/<c>ProxyOverride</c>, so the address the user configured
/// in Windows Settings stays the single source of truth and turning the proxy
/// back on restores their exact configuration.
/// </para>
/// <para>
/// MSIX note: packaged apps get HKCU registry writes virtualized away unless
/// the manifest opts out (<c>unvirtualizedResources</c> capability +
/// <c>desktop6:RegistryWriteVirtualization</c> disabled — see
/// Package.appxmanifest). The symptom of that opt-out missing is "no errors,
/// no effect": the write lands in the package-private view and the real
/// system proxy never moves.
/// </para>
/// </remarks>
internal static class SystemProxyService
{
    private const string InternetSettingsKeyPath =
        @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";

    private const int InternetOptionSettingsChanged = 39;
    private const int InternetOptionRefresh = 37;

    /// <summary>
    /// Raised whenever the proxy state actually changed — after a successful
    /// in-process toggle AND after an external change (Windows Settings, any
    /// other proxy tool) picked up by the registry watcher. Raised on the
    /// writing/watching thread; UI subscribers must marshal themselves.
    /// </summary>
    public static event Action? StateChanged;

    public sealed record SystemProxyState(bool Enabled, string Server, string Bypass)
    {
        public bool HasServer => !string.IsNullOrWhiteSpace(Server);
    }

    public static SystemProxyState Read()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(InternetSettingsKeyPath);
        if (key is null)
        {
            return new SystemProxyState(Enabled: false, Server: string.Empty, Bypass: string.Empty);
        }

        int enabled = key.GetValue("ProxyEnable") is int value ? value : 0;
        return new SystemProxyState(
            Enabled: enabled != 0,
            Server: key.GetValue("ProxyServer") as string ?? string.Empty,
            Bypass: key.GetValue("ProxyOverride") as string ?? string.Empty);
    }

    /// <summary>
    /// Flips the manual-proxy switch. Refuses to enable when no proxy server
    /// is configured — an enabled-but-empty proxy would black-hole all
    /// WinINet traffic. Returns false (and changes nothing) in that case or
    /// when the registry write fails.
    /// </summary>
    public static bool TrySetEnabled(bool enable)
    {
        if (enable && !Read().HasServer)
        {
            App.Log("[SystemProxy] Refusing to enable with an empty ProxyServer");
            return false;
        }

        try
        {
            using RegistryKey? key =
                Registry.CurrentUser.OpenSubKey(InternetSettingsKeyPath, writable: true);
            if (key is null)
            {
                App.Log("[SystemProxy] Internet Settings key missing; nothing to toggle");
                return false;
            }

            key.SetValue("ProxyEnable", enable ? 1 : 0, RegistryValueKind.DWord);
        }
        catch (Exception ex)
        {
            App.Log($"[SystemProxy] Registry write failed: {ex}");
            return false;
        }

        // Registry writes alone are invisible to running apps and the Settings
        // UI until WinINet is told to re-read its configuration.
        NotifyWinInetSettingsChanged();

        App.Log($"[SystemProxy] Manual proxy → {(enable ? "on" : "off")}");
        s_lastNotified = Read();
        StateChanged?.Invoke();
        return true;
    }

    private static void NotifyWinInetSettingsChanged()
    {
        _ = InternetSetOption(IntPtr.Zero, InternetOptionSettingsChanged, IntPtr.Zero, 0);
        _ = InternetSetOption(IntPtr.Zero, InternetOptionRefresh, IntPtr.Zero, 0);
    }

    // ─── Registry watcher ──────────────────────────────────────
    //
    // The card mirrors live system state, but desktop widgets never get
    // activated and only re-read on reveal — so changes made outside this
    // process (Windows Settings, other proxy tools) would never reach the
    // card without a watcher. RegNotifyChangeKeyValue arms an asynchronous
    // notification on the Internet Settings key; a background thread waits on
    // it and raises StateChanged whenever the snapshot actually changed.

    private const int RegNotifyChangeLastSet = 0x4;

    private static int s_watcherStarted;
    private static SystemProxyState s_lastNotified = new(false, string.Empty, string.Empty);

    /// <summary>
    /// Idempotent start of the registry watcher. Called by the widget view
    /// model; the watcher thread lives for the process lifetime and is a
    /// background thread, so it never blocks shutdown.
    /// </summary>
    public static void EnsureWatcherStarted()
    {
        if (Interlocked.CompareExchange(ref s_watcherStarted, 1, 0) != 0)
        {
            return;
        }

        var thread = new Thread(WatchRegistryLoop)
        {
            IsBackground = true,
            Name = "DeskBox-SystemProxyWatch"
        };
        thread.Start();
    }

    private static void WatchRegistryLoop()
    {
        try
        {
            // The notification is tied to this key handle, so it must stay
            // open for as long as the watcher runs.
            using RegistryKey? watchKey =
                Registry.CurrentUser.OpenSubKey(InternetSettingsKeyPath);
            if (watchKey is null)
            {
                App.Log("[SystemProxy] Watcher: Internet Settings key missing");
                return;
            }

            using var changed = new AutoResetEvent(false);
            s_lastNotified = Read();

            while (true)
            {
                int rc = RegNotifyChangeKeyValue(
                    watchKey.Handle.DangerousGetHandle(),
                    watchSubtree: false,
                    RegNotifyChangeLastSet,
                    changed.SafeWaitHandle.DangerousGetHandle(),
                    fAsynchronous: true);
                if (rc != 0)
                {
                    App.Log($"[SystemProxy] Watcher arm failed rc={rc}");
                    return;
                }

                if (!changed.WaitOne())
                {
                    continue;
                }

                // Windows Settings rewrites several values in one toggle;
                // coalesce the burst into one read.
                Thread.Sleep(150);

                var state = Read();
                if (state == s_lastNotified)
                {
                    continue;
                }

                s_lastNotified = state;
                App.Log(
                    $"[SystemProxy] Watcher: state → Enabled={state.Enabled} " +
                    $"Server='{state.Server}'");
                StateChanged?.Invoke();
            }
        }
        catch (Exception ex)
        {
            App.Log($"[SystemProxy] Watcher stopped: {ex}");
        }
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern int RegNotifyChangeKeyValue(
        IntPtr hKey,
        bool watchSubtree,
        int dwNotifyFilter,
        IntPtr hEvent,
        bool fAsynchronous);

    [DllImport("wininet.dll", SetLastError = true)]
    private static extern bool InternetSetOption(
        IntPtr hInternet,
        int dwOption,
        IntPtr lpBuffer,
        int dwBufferLength);
}
