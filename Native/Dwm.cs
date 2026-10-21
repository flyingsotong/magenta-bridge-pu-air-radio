using System.Runtime.InteropServices;
using System.Windows.Media;
using Microsoft.Win32;

namespace MagentaBridge.Native;

/// <summary>
/// Windows 11 shell integration: rounded corners, immersive dark title bar and the
/// user's real accent colour, so the app looks like it belongs beside Settings and
/// Explorer instead of shipped in 2010.
/// </summary>
internal static class Dwm
{
    private const int DwmwaUseImmersiveDarkMode = 20;   // 20 on 20H1+, 19 on older builds
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwaSystemBackdropType = 38;

    private const int DwmwcpRound = 2;
    private const int DwmsbtMainWindow = 2;             // Mica

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    public static void ApplyModernFrame(IntPtr hwnd, bool dark)
    {
        try
        {
            var enabled = dark ? 1 : 0;
            if (DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref enabled, sizeof(int)) != 0)
                DwmSetWindowAttribute(hwnd, 19, ref enabled, sizeof(int));

            var round = DwmwcpRound;
            DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref round, sizeof(int));
        }
        catch
        {
            // Older builds simply keep square corners and the system title bar.
        }
    }

    /// <summary>
    /// Requests the Mica backdrop. Only takes effect when the window lets the system
    /// paint the client area; the caller decides whether that is safe.
    /// </summary>
    public static bool TryApplyMica(IntPtr hwnd)
    {
        try
        {
            var mica = DwmsbtMainWindow;
            return DwmSetWindowAttribute(hwnd, DwmwaSystemBackdropType, ref mica, sizeof(int)) == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>True when Windows is in light mode.</summary>
    public static bool IsLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            var value = key?.GetValue("AppsUseLightTheme");
            return value is int i && i != 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>The user's accent colour, or null when the system does not expose one.</summary>
    public static Color? SystemAccentColor()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM");
            if (key?.GetValue("AccentColor") is not int raw) return null;

            // DWM stores AccentColor as 0xAABBGGRR.
            var r = (byte)(raw & 0xFF);
            var g = (byte)((raw >> 8) & 0xFF);
            var b = (byte)((raw >> 16) & 0xFF);
            return Color.FromRgb(r, g, b);
        }
        catch
        {
            return null;
        }
    }
}
