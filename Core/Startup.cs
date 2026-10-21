using Microsoft.Win32;

namespace MagentaBridge.Core;

/// <summary>
/// Registers the app to start with Windows, using the per-user Run key so no elevation is
/// needed. The stored command passes --minimized so booting does not steal focus with a window.
/// </summary>
public static class Startup
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = ProductInfo.StartupValueName;

    /// <summary>The names this product registered under before the per-device split.</summary>
    private static readonly string[] LegacyValueNames = ProductInfo.LegacyStartupValueNames;

    public static string Command =>
        $"\"{Environment.ProcessPath ?? ProductInfo.FileStem + ".exe"}\" --minimized";

    /// <summary>Reads the registry, not the config: the registry is what Windows acts on.</summary>
    public static bool IsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey);
                return key?.GetValue(ValueName) is string value && value.Length > 0;
            }
            catch (Exception ex)
            {
                Log.Warn($"could not read the startup entry: {ex.Message}");
                return false;
            }
        }
    }

    /// <summary>Creates or removes the entry. Returns false when the registry refused.</summary>
    public static bool Apply(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
                            ?? Registry.CurrentUser.CreateSubKey(RunKey);

            if (key == null)
            {
                Log.Warn("could not open the Run key; start-with-Windows not changed");
                return false;
            }

            if (enabled)
            {
                key.SetValue(ValueName, Command);
                foreach (var legacy in LegacyValueNames) key.DeleteValue(legacy, throwOnMissingValue: false);  // no double launch
                Log.Info($"start with Windows enabled -> {Command}");
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
                foreach (var legacy in LegacyValueNames) key.DeleteValue(legacy, throwOnMissingValue: false);
                Log.Info("start with Windows disabled");
            }

            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"startup registration failed: {ex.Message}");
            return false;
        }
    }
}
