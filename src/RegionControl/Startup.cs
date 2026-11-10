using Microsoft.Win32;

namespace RegionControl;

/// <summary>
/// "Start with Windows": one value in HKCU\Software\Microsoft\Windows\CurrentVersion\Run. No admin needed.
/// Only the app itself writes it, when the user ticks the tray menu item. Off by default.
/// </summary>
static class Startup
{
    const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ValueName = "RegionControl";

    static string Command => $"\"{Environment.ProcessPath}\"";

    /// <summary>True when the Run value exists and points at this exe.</summary>
    public static bool IsEnabled
    {
        get
        {
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey(KeyPath);
                return string.Equals(k?.GetValue(ValueName) as string, Command, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }
    }

    /// <summary>Adds or removes the Run value. Returns an error text, or null on success.</summary>
    public static string? Set(bool on)
    {
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(KeyPath);
            if (on) k.SetValue(ValueName, Command, RegistryValueKind.String);
            else k.DeleteValue(ValueName, throwOnMissingValue: false);
            return null;
        }
        catch (Exception ex) { return ex.Message; }
    }
}
