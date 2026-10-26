using System.Text;
using MagentaBridge.Native;

namespace MagentaBridge.Core;

/// <summary>Which radio a side of the panel is showing. The panel latches this itself.</summary>
public enum SideMode { Unknown = -1, Com = 0, Nav = 1 }

public sealed class RadioState
{
    public bool DeviceConnected;
    public string DeviceName = "";
    public string DeviceVersion = "";

    public bool DisplayConnected;
    public string ComPort = "";
    public string DisplayError = "";

    public bool SimConnected;
    public string SimError = "";
    public string SimClientPath = "";
    public string SimClientVersion = "";
    public string AircraftTitle = "";

    public SideMode Side1 = SideMode.Unknown;
    public SideMode Side2 = SideMode.Unknown;

    /// <summary>True when a side's mode came from the last session rather than from the
    /// panel itself. The first report for that side clears it.</summary>
    public bool Assumed1, Assumed2;

    public bool ModesAssumed => Assumed1 || Assumed2;

    public double Com1Active, Com1Standby;
    public double Com2Active, Com2Standby;
    public double Nav1Active, Nav1Standby;
    public double Nav2Active, Nav2Standby;

    public long Inputs;
    public long EventsSent;
    public string LastAction = "";
    public string LastInput = "";

    public bool ModesKnown => Side1 != SideMode.Unknown && Side2 != SideMode.Unknown;

    /// <summary>True once the sim has sent at least one radio frame.</summary>
    public bool RadioDataReceived;

    public string Aircraft => AircraftTitle;
}

/// <summary>
/// The bridge: HID in, serial out to the panel's display, SimConnect to the sim.
///
/// Three independent threads, deliberately. A busy sim must never stall the panel's
/// display refresh, and an unplugged panel must never stall the sim. The panel's own
/// mode keys are latched in its firmware, so the app reads the mode from the hardware
/// instead of guessing or remembering it.
/// </summary>
public sealed class RadioEngine : IDisposable
{
    // Observed HID report layout, from the device's own descriptor:
    //   report id 0x14, 5 bytes, button usages 1..14 in the first two data bytes.
    private const byte ReportId = 0x14;

    // Usage numbers per the panel's physical layout.
    private const int UsageCom1 = 1, UsageNav1 = 2, UsageCom2 = 3, UsageNav2 = 4;
    private const int UsageSwap1 = 5, UsageSwap2 = 6;
    private const int UsageKhzDown1 = 7, UsageKhzUp1 = 8, UsageMhzDown1 = 9, UsageMhzUp1 = 10;
    private const int UsageKhzDown2 = 11, UsageKhzUp2 = 12, UsageMhzDown2 = 13, UsageMhzUp2 = 14;

    private const int HeartbeatMs = 250;
    private const int TickMs = 60;

    private readonly SimConnectApi _sim = new();
    private readonly Dictionary<string, uint> _eventIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _unmapped = new(StringComparer.Ordinal);

    private readonly object _gate = new();
    private readonly bool[] _buttonDown = new bool[15];
    private readonly bool[] _modeBit = new bool[5];

    private AppConfig _config;
    private Thread? _hidThread, _displayThread, _simThread;
    private volatile bool _stopping;
    private volatile bool _hidLost;
    private HidConnection? _hid;
    private SerialLink? _serial;

    private readonly Dictionary<int, long> _lastDetentAt = new();
    private readonly Dictionary<int, int> _burst = new();

    private readonly object _frameGate = new();
    private DateTime _lastFrameAt = DateTime.MinValue;
    private string _lastFrame = "";

    private readonly HashSet<int> _selfTestSeen = new();
    private uint _nextEventId = 1;
    private DateTime _lastSimAttempt = DateTime.MinValue;
    private DateTime _lastDisplayAttempt = DateTime.MinValue;
    private string _lastDisplayError = "";

    public RadioState State { get; } = new();
    public event Action? Changed;

    /// <summary>When set, transmissions are captured here instead of reaching the sim.</summary>
    public ICollection<(string Name, uint Data)>? Capture { get; set; }

    public bool SelfTestRunning { get; private set; }
    public IReadOnlyCollection<int> SelfTestSeen { get { lock (_gate) return _selfTestSeen.ToArray(); } }

    public AppConfig Config => _config;

    public RadioEngine(AppConfig config)
    {
        _config = config;
        _sim.SimObjectData += OnSimObjectData;
        _sim.ConnectionChanged += connected =>
        {
            State.SimConnected = connected;
            if (connected) State.SimError = "";
            else State.SimError = _sim.LastError ?? "";
            Raise();
        };
    }

    public void ApplyConfig(AppConfig config)
    {
        _config = config;
        Log.Info("engine config replaced; brightness and tuning take effect immediately");
        Raise();
    }

    public void Start()
    {
        Log.Info("radio engine starting");

        _hidThread = new Thread(HidLoop) { IsBackground = true, Name = "HID supervisor" };
        _hidThread.Start();

        _displayThread = new Thread(DisplayLoop) { IsBackground = true, Name = "display link" };
        _displayThread.Start();

        _simThread = new Thread(SimLoop) { IsBackground = true, Name = "SimConnect" };
        _simThread.Start();
    }

    // ---- HID ------------------------------------------------------------

    private void HidLoop()
    {
        while (!_stopping)
        {
            HidDeviceInfo? info = null;
            try
            {
                info = Hid.Find(_config.Device.VendorId, _config.Device.ProductId);
            }
            catch (Exception ex)
            {
                Log.Error("HID enumeration failed", ex);
            }

            if (info == null)
            {
                if (State.DeviceConnected)
                {
                    State.DeviceConnected = false;
                    Log.Info("PU Radio disconnected");
                    Raise();
                }
                Thread.Sleep(1000);
                continue;
            }

            _hidLost = false;

            try
            {
                using var connection = new HidConnection(info);
                _hid = connection;
                connection.ReportReceived += HandleReport;
                connection.Disconnected += reason =>
                {
                    _hidLost = true;
                    Log.Warn($"HID link lost: {reason}");
                };

                // Clear before the reader starts, or a first report could be wiped by it.
                lock (_gate)
                {
                    Array.Clear(_buttonDown);
                    Array.Clear(_modeBit);
                    Array.Clear(_previousModeBit);
                    _raw1 = _raw2 = SideMode.Unknown;
                    _ringArmed[0] = _ringArmed[1] = _ringArmed[2] = _ringArmed[3] = true;
                }

                // The panel only speaks on change, so start from the last session's mode
                // where there is one; it is marked assumed until the panel reports.
                State.Side1 = ParseMode(_config.LastMode1);
                State.Side2 = ParseMode(_config.LastMode2);
                State.Assumed1 = State.Side1 != SideMode.Unknown;
                State.Assumed2 = State.Side2 != SideMode.Unknown;

                connection.Start();
                State.DeviceConnected = true;
                State.DeviceName = info.Display;
                State.DeviceVersion = $"v{info.VersionNumber:X4}";
                Log.Info($"PU Radio connected: {info.Display}, {info.InputReportLength}-byte reports");
                Raise();

                while (!_stopping && !_hidLost)
                    Thread.Sleep(200);
            }
            catch (Exception ex)
            {
                Log.Warn($"HID open failed: {ex.Message}");
            }
            finally
            {
                _hid = null;
                if (State.DeviceConnected)
                {
                    State.DeviceConnected = false;
                    Log.Info("PU Radio disconnected");
                    Raise();
                }
            }

            if (!_stopping) Thread.Sleep(500);
        }
    }

    private void HandleReport(byte[] report)
    {
        if (report.Length < 3) return;

        // The HID stack includes the report id. Accept a bare payload too, so a firmware
        // revision that drops the id does not silently stop the panel.
        var offset = report[0] == ReportId ? 1 : 0;
        if (report.Length < offset + 2) return;

        uint bits = (uint)(report[offset] | (report[offset + 1] << 8));
        Log.Debug($"hid report {bits:X4}");

        lock (_gate)
        {
            State.Inputs++;



            for (var usage = 1; usage <= 4; usage++)
                _modeBit[usage] = (bits & (1u << (usage - 1))) != 0;

            // The rocker's contacts bounce for about a second, so the raw bits are only a
            // proposal. The settled value is promoted by the display loop, which is already
            // ticking; following every bounce would flick the window and the panel's format.
            var side1 = _modeBit[UsageCom1] ? SideMode.Com : _modeBit[UsageNav1] ? SideMode.Nav : SideMode.Unknown;
            var side2 = _modeBit[UsageCom2] ? SideMode.Com : _modeBit[UsageNav2] ? SideMode.Nav : SideMode.Unknown;

            var now = Environment.TickCount64;
            if (side1 != _raw1) { _raw1 = side1; _raw1At = now; }
            if (side2 != _raw2) { _raw2 = side2; _raw2At = now; }

            // The mode switch is a rocker with a COM side and a NAV side. Only the bit that
            // becomes set is the key the user pressed; the cleared one is the old selection.
            if (SelfTestRunning)
            {
                for (var usage = 1; usage <= 4; usage++)
                    if (_modeBit[usage] && !_previousModeBit[usage])
                        _selfTestSeen.Add(usage);
            }

            Array.Copy(_modeBit, _previousModeBit, 5);

            // The two knob pushes are ordinary buttons; a second edge inside the debounce
            // window is contact bounce, not a second press.
            for (var usage = UsageSwap1; usage <= UsageSwap2; usage++)
            {
                var down = (bits & (1u << (usage - 1))) != 0;
                var was = _buttonDown[usage];
                _buttonDown[usage] = down;

                if (!down || was) continue;
                if (IsBounced(usage)) continue;

                if (SelfTestRunning) _selfTestSeen.Add(usage);
                Act(usage);
            }

            DecodeRing(0, UsageKhzDown1, UsageKhzUp1, bits);
            DecodeRing(1, UsageMhzDown1, UsageMhzUp1, bits);
            DecodeRing(2, UsageKhzDown2, UsageKhzUp2, bits);
            DecodeRing(3, UsageMhzDown2, UsageMhzUp2, bits);
        }

        Raise();
    }

    private readonly bool[] _ringArmed = { true, true, true, true };
    private readonly Dictionary<int, long> _lastActAt = new();

    /// <summary>
    /// Mechanical encoders do not switch cleanly. The panel frequently reports a moment
    /// where both direction bits are set as the wiper crosses the midpoint; counting that
    /// as one step each way cancels the click, which is exactly the "skipping" the vendor
    /// software shows. So a ring counts at most one step per press, and an ambiguous report
    /// waits for the transition to resolve instead of guessing.
    /// </summary>
    private void DecodeRing(int index, int decUsage, int incUsage, uint bits)
    {
        var dec = (bits & (1u << (decUsage - 1))) != 0;
        var inc = (bits & (1u << (incUsage - 1))) != 0;

        if (!dec && !inc)
        {
            _ringArmed[index] = true;
            return;
        }

        if (dec && inc) return;          // midpoint: wait for the direction to settle
        if (!_ringArmed[index]) return;  // this press has already been counted

        _ringArmed[index] = false;
        var usage = inc ? incUsage : decUsage;

        if (SelfTestRunning) _selfTestSeen.Add(usage);
        Act(usage);
    }

    private bool IsBounced(int usage)
    {
        var now = Environment.TickCount64;
        if (_lastActAt.TryGetValue(usage, out var last) && now - last < 700) return true;
        _lastActAt[usage] = now;
        return false;
    }
    private readonly bool[] _previousModeBit = new bool[5];


    // Debounced mode: raw bits from the rocker, and the value that has been stable long
    // enough to be believed.
    private SideMode _raw1 = SideMode.Unknown, _raw2 = SideMode.Unknown;
    private long _raw1At, _raw2At;

    /// <summary>How long the rocker must sit still before its position is believed.</summary>
    private const int ModeSettleMs = 220;

    private void PromoteModes()
    {
        var now = Environment.TickCount64;
        var changed = false;

        if (_raw1 != SideMode.Unknown && _raw1 != State.Side1 && now - _raw1At >= ModeSettleMs)
        {
            State.Side1 = _raw1;
            State.Assumed1 = false;
            changed = true;
        }

        if (_raw2 != SideMode.Unknown && _raw2 != State.Side2 && now - _raw2At >= ModeSettleMs)
        {
            State.Side2 = _raw2;
            State.Assumed2 = false;
            changed = true;
        }

        if (!changed) return;

        // Remember where the panel was left, so the next launch can start from it.
        _config.LastMode1 = State.Side1 == SideMode.Nav ? "nav" : "com";
        _config.LastMode2 = State.Side2 == SideMode.Nav ? "nav" : "com";
        try { _config.Save(); } catch (Exception ex) { Log.Warn($"could not save the panel mode: {ex.Message}"); }

        Log.Info($"panel mode: {ModeLabel(1, State.Side1)} / {ModeLabel(2, State.Side2)}" +
                 (State.ModesAssumed ? " (assumed)" : ""));
        Raise();
    }

    private static SideMode ParseMode(string saved) => saved switch
    {
        "com" => SideMode.Com,
        "nav" => SideMode.Nav,
        _ => SideMode.Unknown,
    };

    private static string ModeLabel(int side, SideMode mode) => mode switch
    {
        SideMode.Com => side == 1 ? "COM1" : "COM2",
        SideMode.Nav => side == 1 ? "NAV1" : "NAV2",
        _ => "-",
    };

    private void MarkModeChange(SideMode before1, SideMode before2)
    {
        if (before1 != State.Side1 && State.Side1 != SideMode.Unknown)
            _selfTestSeen.Add(State.Side1 == SideMode.Com ? UsageCom1 : UsageNav1);

        if (before2 != State.Side2 && State.Side2 != SideMode.Unknown)
            _selfTestSeen.Add(State.Side2 == SideMode.Com ? UsageCom2 : UsageNav2);
    }

    // ---- actions --------------------------------------------------------

    private void Act(int usage)
    {
        var label = Describe(usage);
        if (label.Length == 0) return;

        var multiplier = Multiplier(usage);
        // Side by explicit usage, not by number: the bottom knob push is usage 6, which
        // sorts below the bottom rings (11-14) and would otherwise act on side 1.
        var side = usage is UsageCom2 or UsageNav2 or UsageSwap2
            or UsageKhzDown2 or UsageKhzUp2 or UsageMhzDown2 or UsageMhzUp2
            ? 2 : 1;

        // Before the rocker has settled there is still a freshly decoded position, which is
        // the truthful one for an action that arrives with it in the same report.
        var mode = side == 1
            ? (State.Side1 != SideMode.Unknown ? State.Side1 : _raw1)
            : (State.Side2 != SideMode.Unknown ? State.Side2 : _raw2);

        State.LastInput = label;
        State.LastAction = label;
        Log.Info($"input: {label}" + (multiplier > 1 ? $" x{multiplier}" : ""));

        if (SelfTestRunning) return;

        if (!State.SimConnected)
        {
            State.LastAction = $"{label} - sim not connected";
            return;
        }

        if (usage is UsageSwap1 or UsageSwap2)
        {
            if (_config.Tuning.SwapOnPush) SendSwap(side, mode);
        }
        else
        {
            SendSteps(side, mode, usage, multiplier);
        }
    }

    private string Describe(int usage)
    {
        var side1 = State.Side1 == SideMode.Nav ? "NAV1" : "COM1";
        var side2 = State.Side2 == SideMode.Nav ? "NAV2" : "COM2";

        return usage switch
        {
            UsageSwap1 => $"{side1} swap",
            UsageSwap2 => $"{side2} swap",
            UsageKhzDown1 => $"{side1} kHz down",
            UsageKhzUp1 => $"{side1} kHz up",
            UsageMhzDown1 => $"{side1} MHz down",
            UsageMhzUp1 => $"{side1} MHz up",
            UsageKhzDown2 => $"{side2} kHz down",
            UsageKhzUp2 => $"{side2} kHz up",
            UsageMhzDown2 => $"{side2} MHz down",
            UsageMhzUp2 => $"{side2} MHz up",
            _ => "",
        };
    }

    /// <summary>
    /// A deliberate slow turn is exactly one step per detent; a fast spin earns a
    /// multiplier so crossing a band does not take ten seconds. Measured on arrival, so a
    /// pause resets the burst. The original multiplied by 10 after five clicks, which
    /// overshoots the whole band in one flick.
    /// </summary>
    private int Multiplier(int usage)
    {
        if (!_config.Tuning.FastSpin) return 1;

        var now = Environment.TickCount64;
        var last = _lastDetentAt.TryGetValue(usage, out var t) ? t : long.MinValue;
        _lastDetentAt[usage] = now;

        var gap = last == long.MinValue ? int.MaxValue : (int)(now - last);
        var burst = gap < 90 ? _burst.GetValueOrDefault(usage) + 1 : 0;
        _burst[usage] = burst;

        if (burst >= 12) return 4;
        if (burst >= 7) return 3;
        if (burst >= 3) return 2;
        return 1;
    }

    private void SendSwap(int side, SideMode mode)
    {
        if (mode == SideMode.Unknown) return;

        var name = (side, mode) switch
        {
            (1, SideMode.Com) => "COM_STBY_RADIO_SWAP",
            (2, SideMode.Com) => "COM2_RADIO_SWAP",
            (1, SideMode.Nav) => "NAV1_RADIO_SWAP",
            (2, SideMode.Nav) => "NAV2_RADIO_SWAP",
            _ => "",
        };

        Transmit(name);
    }

    private void SendSteps(int side, SideMode mode, int usage, int multiplier)
    {
        var band = usage is UsageKhzDown1 or UsageKhzUp1 or UsageKhzDown2 or UsageKhzUp2 ? "FRACT" : "WHOLE";
        var up = usage is UsageKhzUp1 or UsageMhzUp1 or UsageKhzUp2 or UsageMhzUp2;

        var prefix = (side, mode) switch
        {
            (1, SideMode.Com) => "COM_RADIO_",
            (2, SideMode.Com) => "COM2_RADIO_",
            (1, SideMode.Nav) => "NAV1_RADIO_",
            (2, SideMode.Nav) => "NAV2_RADIO_",
            _ => "",
        };

        if (prefix.Length == 0) return;

        var name = prefix + band + (up ? "_INC" : "_DEC");
        for (var i = 0; i < multiplier; i++) Transmit(name);
    }

    private void Transmit(string name)
    {
        if (name.Length == 0) return;

        if (Capture != null)
        {
            Capture.Add((name, 1));
            State.EventsSent++;
            return;
        }

        if (!_sim.Connected || !_eventIds.TryGetValue(name, out var id))
        {
            if (!_unmapped.Contains(name))
            {
                _unmapped.Add(name);
                Log.Warn($"event {name} is not available in this sim session");
            }
            return;
        }

        _sim.Transmit(id);
        State.EventsSent++;
    }

    // ---- panel display --------------------------------------------------

    private void DisplayLoop()
    {
        while (!_stopping)
        {
            PromoteModes();

            if (!State.DisplayConnected)
            {
                if ((DateTime.UtcNow - _lastDisplayAttempt).TotalMilliseconds >= 1500)
                {
                    _lastDisplayAttempt = DateTime.UtcNow;
                    TryOpenDisplay();
                }

                if (!State.DisplayConnected)
                {
                    Thread.Sleep(TickMs);
                    continue;
                }
            }

            try
            {
                var frame = BuildFrame();
                if (frame != null)
                {
                    var now = DateTime.UtcNow;
                    var due = (now - _lastFrameAt).TotalMilliseconds >= HeartbeatMs;

                    lock (_frameGate)
                    {
                        if ((frame != _lastFrame || due) && _serial != null && _serial.Write(frame))
                        {
                            _lastFrame = frame;
                            _lastFrameAt = now;
                        }
                    }
                }

                if (_serial != null && !_serial.IsOpen)
                {
                    MarkDisplayLost("the display link closed");
                    _serial = null;
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"display loop: {ex.Message}");
                MarkDisplayLost(ex.Message);
                _serial = null;
            }

            Thread.Sleep(TickMs);
        }
    }

    private void TryOpenDisplay()
    {
        SerialPortInfo? info = null;
        try
        {
            info = SerialPortFinder.Find(_config.Device.VendorId, _config.Device.ProductId);
        }
        catch (Exception ex)
        {
            Log.Warn($"serial enumeration failed: {ex.Message}");
        }

        if (info == null)
        {
            // The USB device is absent; the HID side reports that separately.
            State.DisplayError = "";
            State.ComPort = "";
            return;
        }

        if (_config.Device.ComPort is { Length: > 0 } pinned)
            info = info with { PortName = pinned };

        State.ComPort = info.PortName;

        try
        {
            var link = new SerialLink();
            link.Open(info.PortName);
            _serial = link;
            State.DisplayConnected = true;
            State.DisplayError = "";
            _lastDisplayError = "";
            lock (_frameGate) { _lastFrame = ""; }

            Log.Info($"display link established on {info.PortName} ({info.Description})");
            Raise();
        }
        catch (Exception ex)
        {
            var text = Explain(ex, info.PortName);
            if (text != _lastDisplayError)
            {
                _lastDisplayError = text;
                State.DisplayError = text;
                Log.Warn(text);
                Raise();
            }
        }
    }

    private static string Explain(Exception ex, string port)
    {
        var message = ex.Message;
        if (message.Contains("denied", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("access", StringComparison.OrdinalIgnoreCase))
        {
            return $"{port} is open in another program - PU CONNECT or another radio app is probably running.";
        }

        return $"{port}: {message}";
    }

    private void MarkDisplayLost(string reason)
    {
        if (!State.DisplayConnected) return;
        State.DisplayConnected = false;
        State.DisplayError = reason;
        Log.Warn($"display link lost: {reason}");
        Raise();
    }

    /// <summary>
    /// One frame, exactly as the firmware parses it:
    /// RADIO,&lt;brightness&gt;,&lt;act1 kHz&gt;,&lt;stb1 kHz&gt;,&lt;act2 kHz&gt;,&lt;stb2 kHz&gt;,&lt;opt1&gt;,&lt;opt2&gt;,&lt;disp&gt;
    /// COM is 11/21 and NAV is 10/20; the panel prints COM as the last five digits of the
    /// six-digit frequency, so 118700 shows as "18.700" with the leading 1 understood.
    /// </summary>
    private string? BuildFrame()
    {
        var brightness = _config.Display.PanelBrightness;
        var displayBrightness = _config.Display.DisplayBrightness;

        // Anything that is not live frequency data gets the idle frame: before the sim starts,
        // in the menus, after it quits, and before the panel has reported its mode after a
        // power cycle. That is where the clock lives.
        if (!State.ModesKnown || !State.SimConnected || !State.RadioDataReceived)
            return BuildIdleFrame(brightness, displayBrightness);

        double a1, s1, a2, s2;
        lock (_gate)
        {
            (a1, s1) = State.Side1 == SideMode.Nav
                ? (State.Nav1Active, State.Nav1Standby)
                : (State.Com1Active, State.Com1Standby);
            (a2, s2) = State.Side2 == SideMode.Nav
                ? (State.Nav2Active, State.Nav2Standby)
                : (State.Com2Active, State.Com2Standby);
        }

        var opt1 = State.Side1 == SideMode.Nav ? "10" : "11";
        var opt2 = State.Side2 == SideMode.Nav ? "20" : "21";

        return $"RADIO,{brightness},{Khz(a1)},{Khz(s1)},{Khz(a2)},{Khz(s2)},{opt1},{opt2},{displayBrightness}\n";
    }

    /// <summary>
    /// The idle frame: local time, Zulu time, local date, Zulu date. The panel's COM format is
    /// two integer digits and three decimals, so a clock reads as HH.MM0 - the spare digit is
    /// forced to zero because the hardware has no way to blank a position.
    /// </summary>
    private string BuildIdleFrame(int brightness, int displayBrightness)
    {
        if (!_config.Display.ShowClockWhenIdle)
        {
            // A radio head with no data shows zeros, not the last frequency it heard.
            // Brightness 0 does not switch the digits off, so a dim zero is the honest idle
            // state when the clock is switched off.
            if (_config.Display.DimWhenSimDisconnected)
            {
                brightness = Math.Min(brightness, 25);
                displayBrightness = Math.Min(displayBrightness, 5);
            }

            return $"RADIO,{brightness},0,0,0,0,11,21,{displayBrightness}\n";
        }

        var local = DateTime.Now;
        var zulu = DateTime.UtcNow;

        return $"RADIO,{brightness}," +
               $"{ClockValue(local)},{ClockValue(zulu)},{DateValue(local)},{DateValue(zulu)},11,21," +
               $"{displayBrightness}\n";
    }

    /// <summary>HH.MM as the panel will render it: hours x1000 + minutes x10, spare digit zero.</summary>
    public static int ClockValue(DateTime time) => time.Hour * 1000 + time.Minute * 10;

    /// <summary>DD.MM as the panel will render it, spare digit zero.</summary>
    public static int DateValue(DateTime time) => time.Day * 1000 + time.Month * 10;

    private static int Khz(double mhz)
    {
        if (double.IsNaN(mhz) || double.IsInfinity(mhz) || mhz < 0) return 0;
        return (int)Math.Round(mhz * 1000.0, MidpointRounding.AwayFromZero);
    }

    // ---- SimConnect -----------------------------------------------------

    private void SimLoop()
    {
        if (!_sim.Load(_config.SimConnect))
        {
            State.SimError = _sim.LastError ?? "no SimConnect client";
            Log.Error(State.SimError);
            Raise();
        }

        State.SimClientPath = _sim.ClientPath;
        State.SimClientVersion = _sim.ClientVersion;

        while (!_stopping)
        {
            try
            {
                if (!_sim.Connected)
                {
                    if ((DateTime.UtcNow - _lastSimAttempt).TotalMilliseconds < _config.SimConnect.ReconnectDelayMs)
                    {
                        Thread.Sleep(200);
                        continue;
                    }

                    _lastSimAttempt = DateTime.UtcNow;
                    if (!_sim.Open(_config.SimConnect))
                    {
                        State.SimError = _sim.LastError ?? "";
                        Thread.Sleep(500);
                        continue;
                    }

                    State.SimError = "";
                    MapEvents();
                    DefineData();
                    Log.Info("sim session ready");
                    Raise();
                }

                var result = _sim.CallDispatch();
                if (result != 0)
                {
                    Log.Warn($"SimConnect dispatch ended (0x{result:X8}); will reconnect");
                    _sim.Close();
                    _eventIds.Clear();
                    _unmapped.Clear();
                    State.RadioDataReceived = false;
                    Raise();
                    Thread.Sleep(500);
                    continue;
                }

                Thread.Sleep(2);
            }
            catch (Exception ex)
            {
                Log.Error("sim loop", ex);
                Thread.Sleep(1000);
            }
        }
    }

    private void DefineData()
    {
        var radio = new (string Name, string Units)[]
        {
            ("COM ACTIVE FREQUENCY:1", "MHz"),
            ("COM STANDBY FREQUENCY:1", "MHz"),
            ("COM ACTIVE FREQUENCY:2", "MHz"),
            ("COM STANDBY FREQUENCY:2", "MHz"),
            ("NAV ACTIVE FREQUENCY:1", "MHz"),
            ("NAV STANDBY FREQUENCY:1", "MHz"),
            ("NAV ACTIVE FREQUENCY:2", "MHz"),
            ("NAV STANDBY FREQUENCY:2", "MHz"),
        };

        foreach (var (name, units) in radio)
            _sim.AddToDataDefinition(1, name, units, SimConnectApi.DatatypeFloat64);

        _sim.RequestDataOnSimObject(1, 1);

        _sim.AddToDataDefinition(2, "TITLE", null!, SimConnectApi.DatatypeString256);
        _sim.RequestDataOnSimObject(2, 2, SimConnectApi.PeriodSecond);
    }

    private void MapEvents()
    {
        var names = new[]
        {
            "COM_STBY_RADIO_SWAP", "COM2_RADIO_SWAP", "NAV1_RADIO_SWAP", "NAV2_RADIO_SWAP",
            "COM_RADIO_WHOLE_INC", "COM_RADIO_WHOLE_DEC",
            "COM_RADIO_FRACT_INC", "COM_RADIO_FRACT_DEC",
            "COM2_RADIO_WHOLE_INC", "COM2_RADIO_WHOLE_DEC",
            "COM2_RADIO_FRACT_INC", "COM2_RADIO_FRACT_DEC",
            "NAV1_RADIO_WHOLE_INC", "NAV1_RADIO_WHOLE_DEC",
            "NAV1_RADIO_FRACT_INC", "NAV1_RADIO_FRACT_DEC",
            "NAV2_RADIO_WHOLE_INC", "NAV2_RADIO_WHOLE_DEC",
            "NAV2_RADIO_FRACT_INC", "NAV2_RADIO_FRACT_DEC",
        };

        foreach (var name in names)
        {
            var id = _nextEventId++;
            if (_sim.MapEvent(id, name)) _eventIds[name] = id;
            else _unmapped.Add(name);
        }

        Log.Info($"{_eventIds.Count} of {names.Length} radio events mapped");
    }

    private void OnSimObjectData(uint defineId, byte[] payload)
    {
        try
        {
            if (defineId == 1)
            {
                if (payload.Length < 8 * 8) return;

                lock (_gate)
                {
                    State.Com1Active = BitConverter.ToDouble(payload, 0);
                    State.Com1Standby = BitConverter.ToDouble(payload, 8);
                    State.Com2Active = BitConverter.ToDouble(payload, 16);
                    State.Com2Standby = BitConverter.ToDouble(payload, 24);
                    State.Nav1Active = BitConverter.ToDouble(payload, 32);
                    State.Nav1Standby = BitConverter.ToDouble(payload, 40);
                    State.Nav2Active = BitConverter.ToDouble(payload, 48);
                    State.Nav2Standby = BitConverter.ToDouble(payload, 56);
                    State.RadioDataReceived = true;
                }

                Raise();
            }
            else if (defineId == 2)
            {
                var text = Encoding.ASCII.GetString(payload).TrimEnd('\0', ' ');
                if (text == State.AircraftTitle) return;

                var first = State.AircraftTitle.Length == 0;
                State.AircraftTitle = text;
                if (first) Log.Info($"aircraft: {text}");
                else Log.Info($"aircraft changed: {text}");
                Raise();
            }
        }
        catch (Exception ex)
        {
            Log.Error("sim data parse", ex);
        }
    }

    // ---- self test ------------------------------------------------------

    public void BeginSelfTest()
    {
        lock (_gate)
        {
            SelfTestRunning = true;
            _selfTestSeen.Clear();
            Array.Copy(_modeBit, _previousModeBit, 5);

            // A control that is physically held right now counts as seen; everything else
            // waits for its press. A latched mode key is not seeded: the test wants the
            // press itself, and the panel reports the clear edge even for the active key.
            foreach (var (usage, _) in SelfTestUsages())
                if (_buttonDown[usage]) _selfTestSeen.Add(usage);
        }

        Log.Info("panel self-test started");
        Raise();
    }

    public void EndSelfTest()
    {
        SelfTestRunning = false;
        Log.Info("panel self-test ended");
        Raise();
    }

    /// <summary>Every control the panel can report, in the order a person should press them.</summary>
    public static IReadOnlyList<(int Usage, string Label)> SelfTestUsages() => new (int, string)[]
    {
        (UsageCom1, "Top switch - COM1"),
        (UsageNav1, "Top switch - NAV1"),
        (UsageCom2, "Bottom switch - COM2"),
        (UsageNav2, "Bottom switch - NAV2"),
        (UsageSwap1, "Top knob push"),
        (UsageSwap2, "Bottom knob push"),
        (UsageKhzDown1, "Top inner, anticlockwise"),
        (UsageKhzUp1, "Top inner, clockwise"),
        (UsageMhzDown1, "Top outer, anticlockwise"),
        (UsageMhzUp1, "Top outer, clockwise"),
        (UsageKhzDown2, "Bottom inner, anticlockwise"),
        (UsageKhzUp2, "Bottom inner, clockwise"),
        (UsageMhzDown2, "Bottom outer, anticlockwise"),
        (UsageMhzUp2, "Bottom outer, clockwise"),
    };

    /// <summary>Test hook: feed a report through the real decode path without hardware.</summary>
    public void FeedReport(byte[] report)
    {
        _hidLost = false;
        HandleReport(report);
    }

    private void Raise() => Changed?.Invoke();

    public void Dispose()
    {
        _stopping = true;
        try { _hid?.Dispose(); } catch { /* ignore */ }
        try { _serial?.Dispose(); } catch { /* ignore */ }
        try { _sim.Dispose(); } catch { /* ignore */ }
        try { _hidThread?.Join(1500); } catch { /* ignore */ }
        try { _displayThread?.Join(500); } catch { /* ignore */ }
        try { _simThread?.Join(500); } catch { /* ignore */ }
    }
}
