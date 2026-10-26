using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MagentaBridge.Core;

public sealed class DeviceOptions
{
    /// <summary>The PU Radio's USB identifiers. Fixed by the hardware; here so a second unit
    /// model can be pointed at without a rebuild.</summary>
    public int VendorId { get; set; } = 0x2777;
    public int ProductId { get; set; } = 0x8777;

    /// <summary>Null means "the port assigned to that USB device". Set only to override.</summary>
    public string? ComPort { get; set; }
}

public sealed class DisplayOptions
{
    /// <summary>The firmware's LED brightness, 0-250. The vendor shipped 50, which is dim.</summary>
    public int PanelBrightness { get; set; } = 200;

    /// <summary>The second brightness field the firmware takes. The vendor shipped 5.</summary>
    public int DisplayBrightness { get; set; } = 5;

    /// <summary>Dim the panel's digits while the sim is not running, rather than leaving
    /// the last frequencies at full brightness as if they were live. Only applies when the
    /// idle clock is switched off, since a clock is content and should be readable.</summary>
    public bool DimWhenSimDisconnected { get; set; } = true;

    /// <summary>Show local and Zulu time on the panel whenever there are no live
    /// frequencies to show: before the sim connects, in the menus, and after it quits.</summary>
    public bool ShowClockWhenIdle { get; set; } = true;
}

public sealed class TuningOptions
{
    /// <summary>Knob push transfers active and standby, as on the real radio.</summary>
    public bool SwapOnPush { get; set; } = true;

    /// <summary>Spin faster than a slow turn and a detent counts for more than one step.
    /// Off means exactly one step per detent, which is what a slow hand wants anyway.</summary>
    public bool FastSpin { get; set; } = false;
}

public sealed class LogOptions
{
    public string Level { get; set; } = "Info";
    public long MaxBytes { get; set; } = 5L * 1024 * 1024;
    public int MaxFiles { get; set; } = 5;
}

public sealed class SimConnectOptions
{
    /// <summary>
    /// Optional explicit path to a SimConnect client DLL. When null the app probes the
    /// sim install and its own folder instead of trusting a bundled copy.
    /// </summary>
    public string? ClientDllPath { get; set; }
    public int ReconnectDelayMs { get; set; } = 2000;
    public string ClientName { get; set; } = ProductInfo.SimClientName;
}

public sealed class AppConfig
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;

    /// <summary>Start with Windows (per-user Run key), minimised to the tray.</summary>
    public bool StartWithWindows { get; set; }

    /// <summary>"system" follows Windows; "dark" or "light" pin the palette.</summary>
    public string Theme { get; set; } = "system";

    public bool AlwaysOnTop { get; set; }

    /// <summary>Open straight to the tray instead of showing the window. Applies to every
    /// launch, including the Start menu; the tray icon brings the window back.</summary>
    public bool StartMinimized { get; set; }

    /// <summary>The last COM/NAV position each row was left in ("com" or "nav"). The panel
    /// keeps its mode in firmware and only reports it on change, so these let the display
    /// come up right at launch instead of waiting for a touch. They are assumed, not read;
    /// the first report corrects them.</summary>
    public string LastMode1 { get; set; } = "";

    public string LastMode2 { get; set; } = "";

    /// <summary>True once the first-run welcome card has been dismissed.</summary>
    public bool WelcomeDismissed { get; set; }

    public DeviceOptions Device { get; set; } = new();
    public DisplayOptions Display { get; set; } = new();
    public TuningOptions Tuning { get; set; } = new();
    public LogOptions Logging { get; set; } = new();
    public SimConnectOptions SimConnect { get; set; } = new();

    [JsonIgnore]
    public string Path { get; private set; } = "";

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string DefaultDirectory => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        ProductInfo.FamilyFolder, ProductInfo.DeviceFolder);

    public static string DefaultPath => System.IO.Path.Combine(DefaultDirectory, "config.json");

    public static AppConfig LoadOrCreate(string? path = null)
    {
        var target = path ?? DefaultPath;

        if (File.Exists(target))
        {
            try
            {
                var loaded = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(target), ReadOptions);
                if (loaded != null)
                {
                    loaded.Path = target;

                    if (loaded.Version < CurrentVersion)
                    {
                        // A newer build has settings this file cannot know about. Archive it so
                        // the user can diff, then fall through to a freshly generated default.
                        try { File.Copy(target, target + $".v{loaded.Version}.bak", overwrite: true); }
                        catch { /* best effort */ }
                        Log.Warn($"config version {loaded.Version} is older than {CurrentVersion}; " +
                                 "archived and regenerated with current defaults");
                    }
                    else
                    {
                        loaded.MergeWithDefaults();
                        return loaded;
                    }
                }
            }
            catch (Exception ex)
            {
                // A broken config must not brick the app: fall back to defaults and keep
                // the bad file beside it so the user can diff it.
                try { File.Copy(target, target + ".invalid", overwrite: true); } catch { /* best effort */ }
                Log.Error($"config at {target} could not be read; using defaults", ex);
            }
        }

        var created = new AppConfig { Path = target };
        try { created.Save(); }
        catch (Exception ex) { Log.Error("could not write default config", ex); }
        return created;
    }

    public void Save(string? path = null)
    {
        var target = path ?? (Path.Length > 0 ? Path : DefaultPath);
        var dir = System.IO.Path.GetDirectoryName(target);
        if (!string.IsNullOrEmpty(dir)) System.IO.Directory.CreateDirectory(dir);
        File.WriteAllText(target, JsonSerializer.Serialize(this, WriteOptions));
    }

    /// <summary>Fills in any section missing from a hand-edited file.</summary>
    private void MergeWithDefaults()
    {
        Device ??= new DeviceOptions();
        Display ??= new DisplayOptions();
        Tuning ??= new TuningOptions();
        Logging ??= new LogOptions();
        SimConnect ??= new SimConnectOptions();

        Display.PanelBrightness = Math.Clamp(Display.PanelBrightness, 0, 250);
        Display.DisplayBrightness = Math.Clamp(Display.DisplayBrightness, 0, 250);
    }
}
