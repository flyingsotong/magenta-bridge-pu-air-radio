namespace MagentaBridge.Core;

/// <summary>
/// Every place this build names itself. Magenta Bridge is a family: each product targets one
/// device and shares the engine, but Windows sees them as separate applications, so the
/// identity must not be duplicated across files and drift. A second product changes this file.
/// </summary>
public static class ProductInfo
{
    /// <summary>What people see: window, tray, release title, Task Manager.</summary>
    public const string DisplayName = "Magenta Bridge for PU Air Radio";

    /// <summary>Alt-Tab and the caption: brand first, device second.</summary>
    public const string WindowTitle = "Magenta Bridge \u2014 PU Air Radio";

    /// <summary>Short device line under the wordmark.</summary>
    public const string DeviceName = "PU Air Radio";

    /// <summary>File-system safe stem: exe, zip and config folder all use it.</summary>
    public const string FileStem = "MagentaBridge-PuAirRadio";

    /// <summary>Brand folder shared by every Magenta Bridge product.</summary>
    public const string FamilyFolder = "MagentaBridge";

    /// <summary>Per-device folder inside the family: keeps two products' config apart.</summary>
    public const string DeviceFolder = "PuAirRadio";

    /// <summary>What the simulator lists in its own logs.</summary>
    public const string SimClientName = "Magenta Bridge (PU Air Radio)";

    /// <summary>Distinct per product, or two builds would fight over the single-instance lock.</summary>
    public const string MutexName = @"Local\MagentaBridge.PuAirRadio";

    /// <summary>Second launches signal this to raise the running window.</summary>
    public const string ShowEventName = @"Local\MagentaBridge.PuAirRadio.Show";

    /// <summary>Run-key value name, so each product toggles its own startup entry.</summary>
    public const string StartupValueName = "Magenta Bridge (PU Air Radio)";

    /// <summary>Taskbar grouping id: two products must not merge into one button.</summary>
    public const string AppUserModelId = "Magenta.Bridge.PuAirRadio";

    /// <summary>Names this app registered under before the per-device split. This product
    /// has never used another name.</summary>
    public static readonly string[] LegacyStartupValueNames = Array.Empty<string>();
}
