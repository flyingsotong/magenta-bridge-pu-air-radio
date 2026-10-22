using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;
using MagentaBridge.Core;
using MagentaBridge.Native;
using WinForms = System.Windows.Forms;

namespace MagentaBridge;

public partial class App : Application
{
    private Mutex? _mutex;
    private RadioEngine? _engine;
    private MainWindow? _window;
    private bool _appLight;
    private WinForms.NotifyIcon? _tray;
    private readonly Dictionary<string, Icon> _trayIcons = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Distinct per product: the taskbar groups by this id, not by the exe path.
        try { Native.Win32.SetCurrentProcessExplicitAppUserModelID(Core.ProductInfo.AppUserModelId); }
        catch (Exception ex) { Core.Log.Warn($"could not set the app id: {ex.Message}"); }

        var config = AppConfig.LoadOrCreate();
        ConfigureLogging(config);
        InstallCrashHandlers();
        ApplyTheme();

        Log.Info("=== Magenta Bridge for PU Air Radio starting ===");
        Log.Info($"OS: {Environment.OSVersion.VersionString}; theme: {(_appLight ? "light" : "dark")}; " +
                 "design: trim-zero");

        // One instance only. Headless modes must never block on a dialog, so this check
        // runs before any window exists.
        _mutex = new Mutex(true, ProductInfo.MutexName, out var isNew);
        if (!isNew)
        {
            // The user asked for the app: raise the instance already running rather than
            // exiting silently. A hidden tray window with no feedback looks like a crash.
            try
            {
                using var show = new EventWaitHandle(false, EventResetMode.AutoReset, ProductInfo.ShowEventName);
                show.Set();
                Log.Info("another instance is running; asked it to show its window");
            }
            catch (Exception ex)
            {
                Log.Warn($"could not signal the running instance: {ex.Message}");
            }

            Shutdown(0);
            return;
        }

        StartShowListener();

        _engine = new RadioEngine(config);
        _engine.Changed += OnEngineChanged;
        _engine.Start();

        // Re-apply now that the config is loaded: the first pass ran before we knew the
        // user's saved theme choice, so it could only follow the Windows setting.
        ApplyTheme();

        // Keep the registry in step with the config. A boot launch passes --minimized, and the
        // setting does the same for every launch.
        Core.Startup.Apply(config.StartWithWindows);
        var minimized = config.StartMinimized ||
                        e.Args.Any(a => a.Equals("--minimized", StringComparison.OrdinalIgnoreCase));

        _window = new MainWindow(_engine);
        CreateTrayIcon();

        if (!minimized && !e.Args.Any(a => a.Equals("--silent", StringComparison.OrdinalIgnoreCase)))
            _window.Show();

        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    /// <summary>
    /// A background thread that dies takes the process with it and leaves no trace. These
    /// handlers make sure the reason reaches the log file first.
    /// </summary>
    private void InstallCrashHandlers()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            Log.Error("FATAL unhandled exception", args.ExceptionObject as Exception
                ?? new Exception(args.ExceptionObject?.ToString() ?? "unknown"));
            Log.Write(LogLevel.Error, $"terminating: {args.IsTerminating}");
        };

        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error("FATAL dispatcher exception", args.Exception);
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error("unobserved task exception", args.Exception);
            args.SetObserved();
        };
    }

    private static void ConfigureLogging(AppConfig config)
    {
        var directory = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            ProductInfo.FamilyFolder, ProductInfo.DeviceFolder, "logs");

        var level = Enum.TryParse<LogLevel>(config.Logging.Level, true, out var parsed) ? parsed : LogLevel.Info;
        Log.Configure(directory, level, config.Logging.MaxBytes, config.Logging.MaxFiles);
    }

    /// <summary>Re-applies the palette after the user changes the theme in the window.</summary>
    public void ReapplyTheme()
    {
        ApplyTheme();
        if (_window != null)
        {
            var helper = new WindowInteropHelper(_window);
            Dwm.ApplyModernFrame(helper.Handle, !_appLight);
        }
    }

    /// <summary>Applies the dark or light variant of the Trim Zero design language.</summary>
    private void ApplyTheme()
    {
        // Explicit choice wins; "system" follows the Windows app theme.
        var light = (_engine?.Config.Theme ?? "system") switch
        {
            "light" => true,
            "dark" => false,
            _ => Dwm.IsLightTheme(),
        };
        _appLight = light;

        Set("Brush.Window", light ? "#F3F1EC" : "#0B0C0F");
        Set("Brush.Surface", light ? "#FFFFFF" : "#121418");
        Set("Brush.Stroke", light ? "#DED8CC" : "#23262D");
        Set("Brush.Control", light ? "#ECE6DA" : "#1A1D22");
        Set("Brush.ControlHover", light ? "#E2DACB" : "#22262C");
        Set("Brush.Text", light ? "#17181A" : "#F2F4F7");
        Set("Brush.Muted", light ? "#6C675E" : "#8A919B");
        Set("Brush.Dim", light ? "#948E84" : "#5B626C");
        Set("Brush.Accent", light ? "#B87A16" : "#F2B23E");
        Set("Brush.AccentHover", light ? "#A76A0D" : "#FFC65C");
        Set("Brush.OnAccent", light ? "#FFFFFF" : "#0A0B0D");
        Set("Brush.Good", light ? "#2E8B62" : "#63D29B");
        Set("Brush.Link", light ? "#A06410" : "#E0A23A");
        Set("Brush.Warn", light ? "#B87A16" : "#F2B23E");
        Set("Brush.Bad", light ? "#C24A40" : "#E45B4F");

        void Set(string key, string hex)
        {
            var color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex)!;
            Resources[key] = new SolidColorBrush(color);
        }
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                ApplyTheme();
                if (_window != null)
                {
                    var helper = new WindowInteropHelper(_window);
                    Dwm.ApplyModernFrame(helper.Handle, !_appLight);
                }
            }));
        }
    }

    private void CreateTrayIcon()
    {
        _tray = new WinForms.NotifyIcon
        {
            Visible = true,
            Text = ProductInfo.DisplayName,
            ContextMenuStrip = null,
        };

        _tray.MouseUp += (_, args) =>
        {
            if (args.Button == WinForms.MouseButtons.Left) ShowWindow();
            else ShowTrayMenu();
        };

        UpdateTray();
    }

    private void ShowTrayMenu()
    {
        var menu = new ContextMenu { Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint };

        menu.Items.Add(Item($"Open {ProductInfo.DisplayName}", ShowWindow));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Open logs", () =>
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = Log.Directory,
                    UseShellExecute = true,
                });
            }
            catch (Exception ex) { Log.Error("could not open logs", ex); }
        }));
        menu.Items.Add(Item("Reload config", ReloadConfig));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Exit", ExitApp));

        menu.IsOpen = true;
    }

    private static MenuItem Item(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        return item;
    }

    private void ShowWindow()
    {
        if (_window == null) return;
        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    /// <summary>Watches for a second launch and raises this window when one arrives.</summary>
    private void StartShowListener()
    {
        var thread = new Thread(() =>
        {
            try
            {
                using var show = new EventWaitHandle(false, EventResetMode.AutoReset, ProductInfo.ShowEventName);

                while (true)
                {
                    if (!show.WaitOne(500)) continue;

                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        try
                        {
                            // The window may have been closed (X) rather than hidden, in which
                            // case Show() throws. Rebuild it instead of crashing the app.
                            if (_window == null || !_window.IsLoaded)
                                _window = new MainWindow(_engine!);

                            _window.Show();
                            _window.WindowState = WindowState.Normal;
                            _window.Activate();
                            _window.Topmost = true;
                            _window.Topmost = false;
                        }
                        catch (Exception ex)
                        {
                            Log.Warn($"could not show the window: {ex.Message}");
                        }
                    }));
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"show listener stopped: {ex.Message}");
            }
        })
        { IsBackground = true, Name = "show listener" };

        thread.Start();
    }

    private void OnEngineChanged()
    {
        Dispatcher.BeginInvoke(new Action(UpdateTray));
    }

    private void UpdateTray()
    {
        if (_tray == null || _engine == null) return;

        var state = _engine.State;
        var key = state.DeviceConnected && state.SimConnected ? "connected"
                : state.DeviceConnected ? "waiting"
                : "standby";

        var color = key switch
        {
            "connected" => System.Drawing.Color.FromArgb(61, 214, 140),
            "waiting" => System.Drawing.Color.FromArgb(245, 185, 66),
            _ => System.Drawing.Color.FromArgb(150, 150, 160),
        };

        if (!_trayIcons.TryGetValue(key, out var icon))
        {
            icon = DrawIcon(color);
            _trayIcons[key] = icon;
        }

        _tray.Icon = icon;
        _tray.Text = key switch
        {
            "connected" => "Magenta Bridge - connected to MSFS",
            "waiting" => "Magenta Bridge - PU Radio ready, waiting for MSFS",
            _ => "Magenta Bridge - waiting for PU Radio",
        };
    }

    /// <summary>Draws the tray glyph at a resolution-appropriate size rather than scaling one bitmap.</summary>
    private static Icon DrawIcon(System.Drawing.Color color)
    {
        const int size = 32;
        using var bitmap = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(System.Drawing.Color.Transparent);

            using var pen = new System.Drawing.Pen(color, 3f);
            g.DrawEllipse(pen, 5, 5, size - 10, size - 10);
            using var fill = new SolidBrush(color);
            g.FillEllipse(fill, size / 2 - 4, size / 2 - 4, 8, 8);
            g.DrawLine(pen, size / 2, 3, size / 2, 8);
        }

        var handle = bitmap.GetHicon();
        return Icon.FromHandle(handle);
    }

    public void ReloadConfig()
    {
        try
        {
            var config = AppConfig.LoadOrCreate();
            ConfigureLogging(config);
            _engine?.ApplyConfig(config);
            ApplyTheme();
            Log.Info("config reloaded");
            _window?.Dispatcher.BeginInvoke(new Action(() => _window?.Activate()));
        }
        catch (Exception ex)
        {
            Log.Error("config reload failed", ex);
        }
    }

    public void ExitApp()
    {
        Log.Info("exit requested");
        _tray?.Dispose();
        _engine?.Dispose();
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        Shutdown(0);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _engine?.Dispose();
        foreach (var icon in _trayIcons.Values) icon.Dispose();
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
