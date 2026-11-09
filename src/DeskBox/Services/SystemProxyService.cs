using System.Runtime.InteropServices;
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
    /// Raised on the writing thread after a successful toggle, so live UI
    /// (card, tray) can resync from the system state. Not raised for external
    /// changes made outside this process.
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
        StateChanged?.Invoke();
        return true;
    }

    private static void NotifyWinInetSettingsChanged()
    {
        _ = InternetSetOption(IntPtr.Zero, InternetOptionSettingsChanged, IntPtr.Zero, 0);
        _ = InternetSetOption(IntPtr.Zero, InternetOptionRefresh, IntPtr.Zero, 0);
    }

    [DllImport("wininet.dll", SetLastError = true)]
    private static extern bool InternetSetOption(
        IntPtr hInternet,
        int dwOption,
        IntPtr lpBuffer,
        int dwBufferLength);
}
