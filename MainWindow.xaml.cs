using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using MagentaBridge.Core;
using MagentaBridge.Native;

namespace MagentaBridge;

public partial class MainWindow : Window
{
    private readonly RadioEngine _engine;
    private readonly Queue<string> _logLines = new();
    private readonly Dictionary<int, (Ellipse Dot, TextBlock Label)> _selfTestRows = new();
    private readonly DispatcherTimer _tick;

    private bool _syncingSettings;
    private bool _selfTestComplete;
    private DateTime _selfTestCompleteAt = DateTime.MinValue;

    public MainWindow(RadioEngine engine)
    {
        _engine = engine;
        InitializeComponent();

        // Alt-Tab, the shell and accessibility all read this; keep it in one place.
        Title = Core.ProductInfo.WindowTitle;

        // Show just the version; the full build belongs in a tooltip, not in the footer.
        VersionText.Text = "v" + (typeof(MainWindow).Assembly.GetName().Version?.ToString(2) ?? "0.1");

        _engine.Changed += OnEngineChanged;
        Log.Line += OnLogLine;

        // The saved choice has to reach the window itself, not just the checkbox.
        Topmost = _engine.Config.AlwaysOnTop;

        SyncSettingsFromConfig();
        BuildSelfTestRows();
        ApplyLockup();
        ChevronDiagnosticsRotate.Angle = -90;    // collapsed: chevron points right
        WelcomeStrip.Visibility = _engine.Config.WelcomeDismissed ? Visibility.Collapsed : Visibility.Visible;

        SourceInitialized += (_, _) => ApplyWin11Frame();

        Loaded += (_, _) => Dispatcher.BeginInvoke(new Action(() =>
        {
            FitWindow();
            Log.Info($"layout: content={BodyScroll.ExtentHeight:F0} viewport={BodyScroll.ViewportHeight:F0} " +
                     $"scrollable={(BodyScroll.ExtentHeight > BodyScroll.ViewportHeight ? "YES" : "no")}");
        }));

        // The engine raises changes from three threads. A single UI timer is simpler and
        // cheaper than marshalling every report, and it caps the redraw rate at 10 Hz.
        _tick = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(100) };
        _tick.Tick += (_, _) => Refresh();
        _tick.Start();

        Closed += (_, _) =>
        {
            _tick.Stop();
            Log.Line -= OnLogLine;
            _engine.Changed -= OnEngineChanged;
        };

        Refresh();
        AppendLog("Magenta Bridge window ready.");
    }

    private bool? _lockupLight;

    /// <summary>Swaps the Magenta lockup for the one that reads on the current background.</summary>
    private void ApplyLockup()
    {
        var light = _engine.Config.Theme switch
        {
            "light" => true,
            "dark" => false,
            _ => Dwm.IsLightTheme(),
        };

        if (_lockupLight == light) return;
        _lockupLight = light;

        var name = light ? "magenta-lockup-light.png" : "magenta-lockup-dark.png";
        MagentaLockup.Source = new System.Windows.Media.Imaging.BitmapImage(
            new Uri($"pack://application:,,,/Assets/{name}", UriKind.Absolute));
    }

    private void ApplyWin11Frame()
    {
        var helper = new WindowInteropHelper(this);

        // Follow the *chosen* theme, not just the Windows setting, or the frame stays dark
        // while the content turns light.
        var light = _engine.Config.Theme switch
        {
            "light" => true,
            "dark" => false,
            _ => Dwm.IsLightTheme(),
        };
        Dwm.ApplyModernFrame(helper.Handle, !light);

        // Remove the small icon from the caption. At 16px the mark reads as a smudge, and
        // the window identifies itself with its own header instead.
        Native.Win32.SendMessage(helper.Handle, 0x0080 /* WM_SETICON */, IntPtr.Zero, IntPtr.Zero);
    }

    private void OnEngineChanged()
    {
        // Refresh itself runs on the timer; this only makes the first change feel immediate.
        if (!Dispatcher.CheckAccess())
            Dispatcher.BeginInvoke(new Action(Refresh), DispatcherPriority.Background);
    }

    private void OnLogLine(string line) => Dispatcher.BeginInvoke(new Action(() => AppendLog(line)));

    private void AppendLog(string line)
    {
        _logLines.Enqueue(line);
        while (_logLines.Count > 200) _logLines.Dequeue();
        LogText.Text = string.Join(Environment.NewLine, _logLines);
        LogScroll.ScrollToEnd();
    }

    // ---- live state -----------------------------------------------------

    private void Refresh()
    {
        var s = _engine.State;

        DeviceDot.Fill = (Brush)FindResource(s.DeviceConnected ? "Brush.Good" : "Brush.Bad");
        SimDot.Fill = (Brush)FindResource(s.SimConnected ? "Brush.Good" : "Brush.Bad");

        StatusLine.Text = BuildStatus(s);

        // The panel keeps its mode to itself until the first report after a power cycle.
        if (!s.DeviceConnected)
        {
            SyncText.Text = "NO PANEL";
            SyncText.Foreground = (Brush)FindResource("Brush.Dim");
        }
        else if (!s.ModesKnown)
        {
            SyncText.Text = "PRESS A PANEL KEY";
            SyncText.Foreground = (Brush)FindResource("Brush.Warn");
        }
        else if (s.ModesAssumed)
        {
            // Remembered from the last session, not read from the panel yet.
            SyncText.Text = $"{SideLabel(1, s.Side1)}  +  {SideLabel(2, s.Side2)}  \u00b7  LAST SESSION";
            SyncText.Foreground = (Brush)FindResource("Brush.Dim");
        }
        else
        {
            SyncText.Text = $"{SideLabel(1, s.Side1)}  +  {SideLabel(2, s.Side2)}";
            SyncText.Foreground = (Brush)FindResource("Brush.Muted");
        }

        var hasData = s.SimConnected && s.RadioDataReceived;

        // The panel shows the clock whenever there are no live frequencies, so the window
        // mirrors that rather than showing dashes next to a panel showing a time.
        if ((!s.ModesKnown || !hasData) && _engine.Config.Display.ShowClockWhenIdle)
        {
            var local = DateTime.Now;
            var zulu = DateTime.UtcNow;

            Header1A.Text = "LOCAL TIME";
            Header1B.Text = "ZULU TIME";
            Header2A.Text = "LOCAL DATE";
            Header2B.Text = "ZULU DATE";

            SetClockReadout(Side1Active, RadioEngine.ClockValue(local));
            SetClockReadout(Side1Standby, RadioEngine.ClockValue(zulu));
            SetClockReadout(Side2Active, RadioEngine.DateValue(local));
            SetClockReadout(Side2Standby, RadioEngine.DateValue(zulu));

            Arrow1.Visibility = Visibility.Collapsed;
            Arrow2.Visibility = Visibility.Collapsed;
        }
        else
        {
            Header1A.Text = "ACTIVE 1";
            Header1B.Text = "STBY 1";
            Header2A.Text = "ACTIVE 2";
            Header2B.Text = "STBY 2";

            Arrow1.Visibility = Visibility.Visible;
            Arrow2.Visibility = Visibility.Visible;

            SetReadout(Side1Active, s.Side1, hasData,
                s.Side1 == SideMode.Nav ? s.Nav1Active : s.Com1Active);
            SetReadout(Side1Standby, s.Side1, hasData,
                s.Side1 == SideMode.Nav ? s.Nav1Standby : s.Com1Standby);
            SetReadout(Side2Active, s.Side2, hasData,
                s.Side2 == SideMode.Nav ? s.Nav2Active : s.Com2Active);
            SetReadout(Side2Standby, s.Side2, hasData,
                s.Side2 == SideMode.Nav ? s.Nav2Standby : s.Com2Standby);
        }

        SetPill(1, s.Side1);
        SetPill(2, s.Side2);

        LastActionText.Text = s.LastAction;

        PanelStateText.Text = s.DeviceConnected
            ? (s.DisplayConnected ? "connected" : "no display link")
            : "not found";
        PanelStateText.Foreground = (Brush)FindResource(
            s.DeviceConnected && s.DisplayConnected ? "Brush.Good" : "Brush.Muted");

        DeviceLine.Text = s.DeviceConnected
            ? $"PU Radio   {s.DeviceVersion}".Trim()
            : "No PU Radio detected";
        DeviceHint.Text = BuildDeviceHint(s);

        UpdateConflictStrip(s);
        UpdateSelfTest(s);
        UpdateDiagnostics(s);
        ApplyLockup();

        CounterText.Text = $"{s.Inputs:N0} inputs";
    }

    private string BuildStatus(RadioState s)
    {
        var parts = new List<string>();

        if (s.DisplayConnected) parts.Add($"display on {s.ComPort}");
        else if (s.DeviceConnected) parts.Add("display link down");
        else parts.Add("radio not connected");

        if (s.SimConnected) parts.Add(string.IsNullOrWhiteSpace(s.Aircraft) ? "MSFS connected" : s.Aircraft);
        else parts.Add("MSFS not running");

        if (s.DeviceConnected && !s.ModesKnown) parts.Add("waiting for the panel's mode");

        return string.Join("  \u00b7  ", parts);
    }

    private static string BuildDeviceHint(RadioState s)
    {
        if (!s.DeviceConnected)
            return "Plug the panel into USB. The app finds it again on its own.";

        if (!s.DisplayConnected)
            return string.IsNullOrWhiteSpace(s.DisplayError)
                ? "The HID side is up; the display port has not opened yet."
                : s.DisplayError;

        return $"Displays are driven on {s.ComPort}. The panel's own mode keys choose what each side shows.";
    }

    private static string SideLabel(int side, SideMode mode) => mode switch
    {
        SideMode.Com => side == 1 ? "COM1" : "COM2",
        SideMode.Nav => side == 1 ? "NAV1" : "NAV2",
        _ => "-",
    };

    /// <summary>The panel renders a value as two integer digits and three decimals.</summary>
    private static string ClockDigits(int value) =>
        (value / 1000.0).ToString("00.000", CultureInfo.InvariantCulture);

    private void SetClockReadout(TextBlock target, int value)
    {
        target.Text = ClockDigits(value);
        target.Foreground = (Brush)FindResource("Brush.Text");
    }
    private void SetReadout(TextBlock target, SideMode mode, bool hasData, double mhz)
    {
        if (!hasData || mode == SideMode.Unknown || mhz <= 0)
        {
            target.Text = "---.---";
            target.Foreground = (Brush)FindResource("Brush.Dim");
            return;
        }

        target.Text = mode == SideMode.Com
            ? mhz.ToString("0.000", CultureInfo.InvariantCulture)
            : mhz.ToString("0.00", CultureInfo.InvariantCulture);

        target.Foreground = (Brush)FindResource("Brush.Text");
    }

    private void SetPill(int side, SideMode mode)
    {
        var comFill = side == 1 ? Side1ComFill : Side2ComFill;
        var navFill = side == 1 ? Side1NavFill : Side2NavFill;
        var comText = side == 1 ? Side1ComText : Side2ComText;
        var navText = side == 1 ? Side1NavText : Side2NavText;

        var isCom = mode == SideMode.Com;
        var isNav = mode == SideMode.Nav;

        comFill.Visibility = isCom ? Visibility.Visible : Visibility.Collapsed;
        navFill.Visibility = isNav ? Visibility.Visible : Visibility.Collapsed;

        comText.Foreground = (Brush)FindResource(isCom ? "Brush.Accent" : "Brush.Dim");
        navText.Foreground = (Brush)FindResource(isNav ? "Brush.Accent" : "Brush.Dim");
    }

    private void UpdateConflictStrip(RadioState s)
    {
        // Only ever about the display link: the HID side failing is a cable, and the status
        // line already says so. A busy COM port is the one problem a user can fix here.
        var show = s.DeviceConnected && !s.DisplayConnected && !string.IsNullOrWhiteSpace(s.DisplayError);

        var wasVisible = ConflictStrip.Visibility == Visibility.Visible;
        ConflictStrip.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (wasVisible != show)
            Dispatcher.BeginInvoke(new Action(FitWindow), DispatcherPriority.Loaded);

        if (!show) return;

        ConflictTitle.Text = "DISPLAY LINK";
        ConflictBody.Text = s.DisplayError;
    }

    private void UpdateDiagnostics(RadioState s)
    {
        ClientText.Text = string.IsNullOrWhiteSpace(s.SimClientPath)
            ? "SimConnect client: not loaded"
            : $"SimConnect client: {s.SimClientPath} ({s.SimClientVersion})";

        AircraftText.Text = string.IsNullOrWhiteSpace(s.Aircraft)
            ? ""
            : $"Aircraft: {s.Aircraft}";

        PortText.Text = s.DeviceConnected
            ? $"HID: {s.DeviceName} {s.DeviceVersion}   \u00b7   display: {(s.DisplayConnected ? s.ComPort : "not open")}"
            : "HID: not found";

        ErrorText.Text = s.SimError;

        var errors = new List<string>();
        if (!string.IsNullOrWhiteSpace(s.DisplayError)) errors.Add(s.DisplayError);
        if (!string.IsNullOrWhiteSpace(s.SimError)) errors.Add(s.SimError);
        ErrorText.Text = string.Join(Environment.NewLine, errors);
    }

    // ---- self test ------------------------------------------------------

    private void BuildSelfTestRows()
    {
        SelfTestGrid.Children.Clear();
        _selfTestRows.Clear();

        foreach (var (usage, label) in RadioEngine.SelfTestUsages())
        {
            var row = new Grid { Margin = new Thickness(0, 3, 16, 3) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var dot = new Ellipse
            {
                Width = 7,
                Height = 7,
                Fill = (Brush)FindResource("Brush.Dim"),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
            };

            var text = new TextBlock
            {
                Text = label,
                FontSize = 11,
                Foreground = (Brush)FindResource("Brush.Muted"),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            Grid.SetColumn(text, 1);

            row.Children.Add(dot);
            row.Children.Add(text);
            SelfTestGrid.Children.Add(row);
            _selfTestRows[usage] = (dot, text);
        }
    }

    private void OnVerifyPanel(object sender, RoutedEventArgs e)
    {
        if (_engine.SelfTestRunning)
        {
            StopSelfTest();
            return;
        }

        WelcomeStrip.Visibility = Visibility.Collapsed;
        SelfTestBody.Visibility = Visibility.Visible;
        _selfTestComplete = false;
        _selfTestCompleteAt = DateTime.MinValue;

        foreach (var (_, (dot, text)) in _selfTestRows)
        {
            dot.Fill = (Brush)FindResource("Brush.Dim");
            text.Foreground = (Brush)FindResource("Brush.Muted");
        }

        SelfTestHeadline.Text = "Press each control in turn";
        SelfTestHint.Text = "Rock the top mode switch to NAV1 then back to COM1, and the bottom switch to NAV2 then back to COM2. " +
                            "Then push both knobs, and turn each ring once in each direction. Nothing is sent to the sim while this runs.";
        SelfTestProgress.Text = "0 of 14";
        PrimaryActionText.Text = "Stop test";

        _engine.BeginSelfTest();
        BodyScroll.ScrollToEnd();
        Dispatcher.BeginInvoke(new Action(FitWindow), DispatcherPriority.Loaded);
    }

    private void StopSelfTest()
    {
        _engine.EndSelfTest();
        PrimaryActionText.Text = "Test your panel";

        if (!_selfTestComplete)
            SelfTestBody.Visibility = Visibility.Collapsed;

        Dispatcher.BeginInvoke(new Action(FitWindow), DispatcherPriority.Loaded);
    }

    private void UpdateSelfTest(RadioState s)
    {
        if (!_engine.SelfTestRunning && !_selfTestComplete) return;

        var seen = _engine.SelfTestSeen;
        var count = 0;

        foreach (var (usage, (dot, text)) in _selfTestRows)
        {
            var hit = seen.Contains(usage);
            if (hit) count++;

            dot.Fill = (Brush)FindResource(hit ? "Brush.Good" : "Brush.Dim");
            text.Foreground = (Brush)FindResource(hit ? "Brush.Text" : "Brush.Muted");
        }

        SelfTestProgress.Text = $"{count} of 14";

        if (_engine.SelfTestRunning && count == 14)
        {
            _engine.EndSelfTest();
            _selfTestComplete = true;
            _selfTestCompleteAt = DateTime.UtcNow;
            SelfTestHeadline.Text = "All 14 controls seen";
            SelfTestHint.Text = "Every mode switch, both knob pushes and all eight rotary directions reported. " +
                                "The panel is wired correctly and this app is reading it.";
            PrimaryActionText.Text = "Test your panel";
            Log.Info("panel self-test: all 14 controls seen");
        }
    }

    // ---- settings -------------------------------------------------------

    private void SyncSettingsFromConfig()
    {
        _syncingSettings = true;
        try
        {
            var cfg = _engine.Config;

            PanelBrightnessSlider.Value = cfg.Display.PanelBrightness;
            PanelBrightnessValue.Text = cfg.Display.PanelBrightness.ToString();
            DisplayBrightnessSlider.Value = cfg.Display.DisplayBrightness;
            DisplayBrightnessValue.Text = cfg.Display.DisplayBrightness.ToString();

            DimWhenDownBox.IsChecked = cfg.Display.DimWhenSimDisconnected;
            ClockBox.IsChecked = cfg.Display.ShowClockWhenIdle;
            StartMinimizedBox.IsChecked = cfg.StartMinimized;
            SwapBox.IsChecked = cfg.Tuning.SwapOnPush;
            FastSpinBox.IsChecked = cfg.Tuning.FastSpin;

            StartWithWindowsBox.IsChecked = Core.Startup.IsEnabled;
            TopmostBox.IsChecked = Topmost;
            DarkThemeBox.IsChecked = cfg.Theme == "dark" ||
                                     (cfg.Theme != "light" && !Dwm.IsLightTheme());

            SettingsConfigText.Text = "Config: " + cfg.Path;
            SettingsVersionText.Text = VersionText.Text;
            SettingsClientText.Text = string.IsNullOrWhiteSpace(_engine.State.SimClientPath)
                ? "SimConnect client: not loaded"
                : _engine.State.SimClientPath;
        }
        finally
        {
            _syncingSettings = false;
        }
    }

    private void OnPanelBrightnessChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncingSettings) return;
        _engine.Config.Display.PanelBrightness = (int)Math.Round(e.NewValue);
        PanelBrightnessValue.Text = _engine.Config.Display.PanelBrightness.ToString();
    }

    private void OnDisplayBrightnessChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncingSettings) return;
        _engine.Config.Display.DisplayBrightness = (int)Math.Round(e.NewValue);
        DisplayBrightnessValue.Text = _engine.Config.Display.DisplayBrightness.ToString();
    }

    private void OnStartMinimizedChanged(object sender, RoutedEventArgs e)
    {
        if (_syncingSettings) return;
        _engine.Config.StartMinimized = StartMinimizedBox.IsChecked == true;
        SaveConfig();
    }

    private void OnClockChanged(object sender, RoutedEventArgs e)
    {
        if (_syncingSettings) return;
        _engine.Config.Display.ShowClockWhenIdle = ClockBox.IsChecked == true;
        Refresh();
    }

    private void OnDimWhenDownChanged(object sender, RoutedEventArgs e)
    {
        if (_syncingSettings) return;
        _engine.Config.Display.DimWhenSimDisconnected = DimWhenDownBox.IsChecked == true;
    }

    private void OnSwapChanged(object sender, RoutedEventArgs e)
    {
        if (_syncingSettings) return;
        _engine.Config.Tuning.SwapOnPush = SwapBox.IsChecked == true;
    }

    private void OnFastSpinChanged(object sender, RoutedEventArgs e)
    {
        if (_syncingSettings) return;
        _engine.Config.Tuning.FastSpin = FastSpinBox.IsChecked == true;
    }

    private void OnStartWithWindowsChanged(object sender, RoutedEventArgs e)
    {
        if (_syncingSettings) return;

        var want = StartWithWindowsBox.IsChecked == true;
        if (want != Core.Startup.IsEnabled && !Core.Startup.Apply(want))
        {
            // Registry refused the change: put the box back where the system actually is.
            _syncingSettings = true;
            StartWithWindowsBox.IsChecked = Core.Startup.IsEnabled;
            _syncingSettings = false;
            return;
        }

        _engine.Config.StartWithWindows = want;
        SaveConfig();
    }

    private void OnTopmostChanged(object sender, RoutedEventArgs e)
    {
        if (_syncingSettings) return;

        Topmost = TopmostBox.IsChecked == true;
        _engine.Config.AlwaysOnTop = Topmost;
        SaveConfig();
    }

    private void OnDarkThemeChanged(object sender, RoutedEventArgs e)
    {
        if (_syncingSettings) return;

        _engine.Config.Theme = DarkThemeBox.IsChecked == true ? "dark" : "light";
        ((App)Application.Current).ReapplyTheme();
        ApplyWin11Frame();
        ApplyLockup();
        SaveConfig();
    }

    private void SaveConfig()
    {
        try { _engine.Config.Save(); }
        catch (Exception ex) { Log.Error("could not save the config", ex); }
    }

    private void OnOpenSettings(object sender, RoutedEventArgs e)
    {
        SyncSettingsFromConfig();
        SettingsOverlay.Visibility = Visibility.Visible;
        Dispatcher.BeginInvoke(new Action(FitHeightForSettings), DispatcherPriority.Loaded);
    }

    private void OnCloseSettings(object sender, RoutedEventArgs e)
    {
        SettingsOverlay.Visibility = Visibility.Collapsed;
        SaveConfig();
        Refresh();
        Dispatcher.BeginInvoke(new Action(FitWindow), DispatcherPriority.Loaded);
    }

    /// <summary>
    /// Grows the window to the settings content. Measured unconstrained, because inside a
    /// star-sized row the panel only reports the space it was given, not the space it needs.
    /// </summary>
    private void FitHeightForSettings()
    {
        var width = Math.Max(360, SettingsOverlay.ActualWidth - 48);
        SettingsContent.Measure(new Size(width, double.PositiveInfinity));
        var wanted = SettingsContent.DesiredSize.Height + 150;
        Height = Math.Min(Math.Max(Height, wanted), SystemParameters.WorkArea.Height - 40);
    }

    /// <summary>
    /// Sizes the window to the cards it is actually showing. Measured, not guessed: the body
    /// grows when the welcome card or the self-test appears, and shrinking keeps the footer
    /// from sitting over a scrollbar for no reason.
    /// </summary>
    private void FitWindow()
    {
        if (BodyScroll.Content is not FrameworkElement body) return;

        var width = Math.Max(360, BodyScroll.ActualWidth - 2);
        body.Measure(new Size(width, double.PositiveInfinity));

        const double chrome = 158;
        var wanted = Math.Ceiling(body.DesiredSize.Height) + chrome;
        Height = Math.Min(Math.Max(620, wanted), SystemParameters.WorkArea.Height - 40);
    }

    // ---- chrome and links ----------------------------------------------

    private void OnMinimise(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximise(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnCloseWindow(object sender, RoutedEventArgs e) => Hide();

    private void OnHide(object sender, RoutedEventArgs e) => Hide();

    private void OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Escape)
        {
            if (SettingsOverlay.Visibility == Visibility.Visible)
            {
                OnCloseSettings(this, new RoutedEventArgs());
                e.Handled = true;
                return;
            }

            Hide();
            e.Handled = true;
        }
        else if (e.Key == System.Windows.Input.Key.L &&
                 (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) != 0)
        {
            try { Clipboard.SetText(string.Join(Environment.NewLine, _logLines)); }
            catch (Exception ex) { Log.Warn($"clipboard copy failed: {ex.Message}"); }
            e.Handled = true;
        }
    }

    private void OnToggleDiagnostics(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        var open = DiagnosticsBody.Visibility != Visibility.Visible;
        DiagnosticsBody.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        ChevronDiagnosticsRotate.Angle = open ? 0 : -90;
    }

    private void OnDismissWelcome(object sender, RoutedEventArgs e)
    {
        WelcomeStrip.Visibility = Visibility.Collapsed;
        _engine.Config.WelcomeDismissed = true;
        SaveConfig();
        Dispatcher.BeginInvoke(new Action(FitWindow), DispatcherPriority.Loaded);
    }

    private void OnReloadConfig(object sender, RoutedEventArgs e)
    {
        ((App)Application.Current).ReloadConfig();
        SyncSettingsFromConfig();
        Refresh();
    }

    private void OnOpenLogs(object sender, System.Windows.Input.MouseButtonEventArgs e)
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
    }

    private void OnCloseVendorApp(object sender, RoutedEventArgs e)
    {
        try
        {
            foreach (var process in System.Diagnostics.Process.GetProcessesByName("PU CONNECT MSFS"))
            {
                process.Kill();
                Log.Info($"closed {process.ProcessName} ({process.Id})");
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"could not close PU CONNECT: {ex.Message}");
        }
    }

    private void OnRecheckLink(object sender, RoutedEventArgs e)
    {
        // The supervisor retries on its own; this gives the impatient a reason to believe.
        Log.Info("display link recheck requested");
        Refresh();
    }

    private void OnSendFeedback(object sender, System.Windows.Input.MouseButtonEventArgs e) =>
        OpenUrl("mailto:alan@magentadebrief.com?subject=Magenta%20Bridge%20(PU%20Radio)");

    private void OnMagenta(object sender, System.Windows.Input.MouseButtonEventArgs e) =>
        OpenUrl("https://magentadebrief.com");

    private void OnDonate(object sender, System.Windows.Input.MouseButtonEventArgs e) =>
        OpenUrl("https://magentadebrief.com/donate");

    private static void OpenUrl(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true,
            });
        }
        catch (Exception ex) { Log.Warn($"could not open {url}: {ex.Message}"); }
    }
}
