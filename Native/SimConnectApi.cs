using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using MagentaBridge.Core;

namespace MagentaBridge.Native;

/// <summary>
/// Minimal SimConnect client. Unlike the original — which shipped a SimConnect.dll from
/// July 2020 beside the exe and never looked at the sim — this resolves a client DLL in a
/// documented order and logs which one it picked and its version (teardown defects 2 and 17).
/// </summary>
public sealed class SimConnectApi : IDisposable
{
    public const uint ObjectIdUser = 0;
    // SIMCONNECT_PERIOD: NEVER=0, ONCE=1, VISUAL_FRAME=2, SIM_FRAME=3, SECOND=4.
    // The original passed 3, i.e. every sim frame, not once a second.
    public const uint PeriodSecond = 4;
    public const uint PeriodVisualFrame = 2;
    public const uint EventFlagGroupIdIsPriority = 0x10;
    public const uint Unused = 0xFFFFFFFF;

    public const uint DatatypeInt32 = 1;
    public const uint DatatypeFloat64 = 4;
    public const uint DatatypeString256 = 9;

    private const int RecvIdSimObjectData = 8;

    private IntPtr _module;
    private IntPtr _handle;

    private OpenDelegate? _open;
    private CloseDelegate? _close;
    private MapDelegate? _map;
    private TransmitDelegate? _transmit;
    private AddToDefinitionDelegate? _addToDefinition;
    private RequestDataDelegate? _requestData;
    private CallDispatchDelegate? _callDispatch;
    private GetLastErrorDelegate? _getLastErrorText;
    private EnumerateInputEventsDelegate? _enumerateInputEvents;
    private SetInputEventDelegate? _setInputEvent;
    private SetInputEventDoubleDelegate? _setInputEventDouble;
    private SubscribeInputEventDelegate? _subscribeInputEvent;
    private EnumerateInputEventParamsDelegate? _enumerateInputEventParams;
    private SetDataOnSimObjectDelegate? _setDataOnSimObject;

    // Rooted so the GC never collects the thunk the sim calls back into.
    private readonly DispatchCallback _dispatchCallback;

    public string ClientPath { get; private set; } = "";
    public string ClientVersion { get; private set; } = "";
    public bool Connected => _handle != IntPtr.Zero;

    /// <summary>True when the loaded client exposes the MSFS Input Events (B-event) API.</summary>
    public bool SupportsInputEvents => _enumerateInputEvents != null && _setInputEvent != null;

    public string InputEventStatus => _enumerateInputEvents == null
        ? "client does not export the Input Events API"
        : _setInputEvent == null ? "enumerate only (cannot set)" : "available";

    /// <summary>Raised for every dispatch message before parsing; used for diagnostics.</summary>
    public event Action<uint, byte[]>? RawRecv;
    public string? LastError { get; private set; }

    /// <summary>Raised on the dispatch thread: define id and raw payload.</summary>
    public event Action<uint, byte[]>? SimObjectData;
    public event Action<bool>? ConnectionChanged;

    public SimConnectApi()
    {
        _dispatchCallback = new DispatchCallback(OnDispatch);
    }

    public static IReadOnlyList<string> CandidateClientPaths(SimConnectOptions options)
    {
        var list = new List<string>();

        if (!string.IsNullOrWhiteSpace(options.ClientDllPath))
            list.Add(options.ClientDllPath!);

        // Diagnostic override: lets a different client be tried without editing config.
        var overridePath = Environment.GetEnvironmentVariable("OCTAVI_SIMCONNECT_DLL");
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            list.Clear();
            list.Add(overridePath!);
        }

        // 1. Beside the app (the user can drop a newer client here deliberately).
        list.Add(Path.Combine(AppContext.BaseDirectory, "SimConnect.dll"));

        // 2. An installed MSFS SDK, which carries the client matching the current sim.
        var sdk = Environment.GetEnvironmentVariable("MSFS_SDK") ?? "";
        if (sdk.Length > 0)
            list.Add(Path.Combine(sdk, "SimConnect SDK", "lib", "SimConnect.dll"));

        // 3. The sim's own content folder on whichever drive it was installed to.
        foreach (var root in SimInstallRoots())
        {
            if (!Directory.Exists(root)) continue;
            list.Add(Path.Combine(root, "Microsoft Flight Simulator 2024", "Content", "SimConnect.dll"));
            list.Add(Path.Combine(root, "Microsoft Flight Simulator", "SimConnect.dll"));
        }

        // 3b. The sim's own client, matched to the installed build. MSFS 2024 ships it as
        //     SimConnect_internal.dll; it exposes the full client API *plus* the Input
        //     Events (B-event) API that the 2020 redistributable lacks. Newest wins.
        foreach (var root in SimInstallRoots())
        {
            if (!Directory.Exists(root)) continue;
            foreach (var sim in new[] { "Microsoft Flight Simulator 2024", "Microsoft Flight Simulator" })
            {
                var content = Path.Combine(root, sim, "Content");
                if (!Directory.Exists(content)) continue;
                foreach (var file in Directory.GetFiles(content, "SimConnect*.dll")
                             .OrderByDescending(File.GetLastWriteTimeUtc))
                {
                    list.Add(file);
                }
            }
        }

        // 4. Last resort: the copy the vendor bundle already left on this machine, so the
        //    panel keeps working before the user has a newer client. Versioned in the log.
        list.Add(@"C:\Program Files\Octavi\SimConnect.dll");

        return list.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Candidate MSFS install roots, discovered rather than hard-coded: the Xbox app puts
    /// the game under a folder named "Xbox" ("XboxGames" on older installs) on whichever
    /// drive the user picked, so sweeping drive roots survives an install being moved.
    /// </summary>
    private static IReadOnlyList<string> SimInstallRoots()
    {
        var roots = new List<string>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.DriveType != DriveType.Fixed || !drive.IsReady) continue;
                foreach (var name in new[] { "Xbox", "XboxGames" })
                {
                    var root = Path.Combine(drive.RootDirectory.FullName, name);
                    if (Directory.Exists(root)) roots.Add(root);
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        roots.Add(@"C:\Program Files\WindowsApps");
        return roots;
    }

    public bool Load(SimConnectOptions options)
    {
        if (_module != IntPtr.Zero) return true;   // already resolved

        foreach (var candidate in CandidateClientPaths(options))
        {
            if (!File.Exists(candidate)) continue;

            _module = Win32.LoadLibraryEx(candidate, IntPtr.Zero, Win32.LoadWithAlteredSearchPath);
            if (_module == IntPtr.Zero)
            {
                Log.Warn($"SimConnect client at {candidate} failed to load (win32 error {Marshal.GetLastWin32Error()})");
                continue;
            }

            ClientPath = candidate;
            try
            {
                var info = FileVersionInfo.GetVersionInfo(candidate);
                ClientVersion = string.IsNullOrWhiteSpace(info.FileVersion)
                    ? $"{new FileInfo(candidate).LastWriteTime:yyyy-MM-dd}"
                    : info.FileVersion!;
            }
            catch
            {
                ClientVersion = "unknown";
            }

            try
            {
                _open = Bind<OpenDelegate>("SimConnect_Open");
                _close = Bind<CloseDelegate>("SimConnect_Close");
                _map = Bind<MapDelegate>("SimConnect_MapClientEventToSimEvent");
                _transmit = Bind<TransmitDelegate>("SimConnect_TransmitClientEvent");
                _addToDefinition = Bind<AddToDefinitionDelegate>("SimConnect_AddToDataDefinition");
                _requestData = Bind<RequestDataDelegate>("SimConnect_RequestDataOnSimObject");
                _callDispatch = Bind<CallDispatchDelegate>("SimConnect_CallDispatch");
                _getLastErrorText = TryBind<GetLastErrorDelegate>("SimConnect_GetLastErrorText");
                _enumerateInputEvents = TryBind<EnumerateInputEventsDelegate>("SimConnect_EnumerateInputEvents");
                _setInputEvent = TryBind<SetInputEventDelegate>("SimConnect_SetInputEvent");
                _setInputEventDouble = TryBind<SetInputEventDoubleDelegate>("SimConnect_SetInputEvent");
                _subscribeInputEvent = TryBind<SubscribeInputEventDelegate>("SimConnect_SubscribeInputEvent");
                _enumerateInputEventParams = TryBind<EnumerateInputEventParamsDelegate>("SimConnect_EnumerateInputEventParams");
                _setDataOnSimObject = TryBind<SetDataOnSimObjectDelegate>("SimConnect_SetDataOnSimObject");
            }
            catch (Exception ex)
            {
                Log.Error($"SimConnect client {candidate} is missing required exports", ex);
                Free();
                continue;
            }

            Log.Info($"SimConnect client resolved: {ClientPath} (version {ClientVersion})");
            return true;
        }

        LastError = "No SimConnect client DLL found. Install the MSFS SDK, or set simConnect.clientDllPath in config.json.";
        Log.Error(LastError);
        return false;
    }

    public bool Open(SimConnectOptions options)
    {
        if (_open == null) { LastError = "SimConnect client not loaded."; return false; }
        if (Connected) return true;

        var name = string.IsNullOrWhiteSpace(options.ClientName) ? Core.ProductInfo.SimClientName : options.ClientName;
        var hr = _open(out var handle, name, IntPtr.Zero, 0, IntPtr.Zero, 0);

        if (hr != 0 || handle == IntPtr.Zero)
        {
            _handle = IntPtr.Zero;
            LastError = $"SimConnect_Open failed (hr=0x{hr:X8}) — is MSFS running?";
            return false;
        }

        _handle = handle;
        LastError = null;
        Log.Info($"SimConnect connected as \"{name}\"");
        ConnectionChanged?.Invoke(true);
        return true;
    }

    public void Close()
    {
        if (_handle == IntPtr.Zero) return;
        try { _close?.Invoke(_handle); } catch { /* host may already be gone */ }
        _handle = IntPtr.Zero;
        Log.Info("SimConnect disconnected");
        ConnectionChanged?.Invoke(false);
    }

    /// <summary>Maps a client event. Returns false for empty names, which the original
    /// happily transmitted (defect 4).</summary>
    public bool MapEvent(uint eventId, string eventName)
    {
        if (string.IsNullOrWhiteSpace(eventName)) return false;
        if (_map == null || _handle == IntPtr.Zero) return false;
        var hr = _map(_handle, eventId, eventName);
        if (hr != 0)
        {
            Log.Warn($"MapClientEventToSimEvent({eventName}) failed hr=0x{hr:X8}");
            return false;
        }
        return true;
    }

    public void Transmit(uint eventId, uint data = 0, uint groupId = 1)
    {
        if (_transmit == null || _handle == IntPtr.Zero) return;
        var hr = _transmit(_handle, ObjectIdUser, eventId, data, groupId, EventFlagGroupIdIsPriority);
        if (hr != 0) Log.Warn($"TransmitClientEvent(id={eventId}) failed hr=0x{hr:X8}");
    }

    public void AddToDataDefinition(uint defineId, string simVar, string units, uint datatype, float epsilon = 0f, uint datumId = Unused)
    {
        if (_addToDefinition == null || _handle == IntPtr.Zero) return;
        var hr = _addToDefinition(_handle, defineId, simVar, units, datatype, epsilon, datumId);
        if (hr != 0) Log.Warn($"AddToDataDefinition({simVar}) failed hr=0x{hr:X8}");
    }

    public void RequestDataOnSimObject(uint requestId, uint defineId)
    {
        // Per visual frame, not per second: at 1 Hz the readout lags the knob by up to a second,
        // which reads as input latency even though events are transmitted immediately.
        RequestDataOnSimObject(requestId, defineId, PeriodVisualFrame);
    }

    public void RequestDataOnSimObject(uint requestId, uint defineId, uint period)
    {
        if (_requestData == null || _handle == IntPtr.Zero) return;
        var hr = _requestData(_handle, requestId, defineId, ObjectIdUser, period, 0, 0, 0, 0);
        if (hr != 0) Log.Warn($"RequestDataOnSimObject(id={requestId}) failed hr=0x{hr:X8}");
    }

    /// <summary>Pumps the message queue. Must be called repeatedly on a dedicated thread.</summary>
    public int CallDispatch()
    {
        if (_callDispatch == null || _handle == IntPtr.Zero) return -1;
        return _callDispatch(_handle, _dispatchCallback, IntPtr.Zero);
    }

    public string? ReadLastErrorText()
    {
        if (_getLastErrorText == null || _handle == IntPtr.Zero) return null;
        try
        {
            var buffer = new StringBuilder(512);
            _getLastErrorText(_handle, buffer, (uint)buffer.Capacity);
            return buffer.ToString();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Asks the sim to enumerate its input events (B-events).</summary>
    public void EnumerateInputEvents(uint requestId)
    {
        if (_enumerateInputEvents == null || _handle == IntPtr.Zero) return;
        var hr = _enumerateInputEvents(_handle, requestId);
        if (hr != 0) Log.Warn($"EnumerateInputEvents failed hr=0x{hr:X8}");
    }

    /// <summary>
    /// Drives a B-event. The SDK declares UINT64 Hash and DWORD cbUnitSize — the size of ONE
    /// value, not the total byte count. Passing a 32-bit hash silently did nothing.
    /// </summary>
    public bool SetInputEvent(ulong hash, float value)
    {
        if (_setInputEvent == null || _handle == IntPtr.Zero) return false;

        var buffer = new[] { value };
        var hr = _setInputEvent(_handle, hash, (uint)sizeof(float), buffer);
        if (hr != 0) Log.Warn($"SetInputEvent(0x{hash:X16}) hr=0x{hr:X8}");
        return hr == 0;
    }

    /// <summary>
    /// Drives a B-event whose descriptor eType is SIMCONNECT_INPUT_EVENT_TYPE_DOUBLE (0), which
    /// is the enum's first member — so a double with cbUnitSize 8 is the expected payload.
    /// </summary>
    public bool SetInputEventDouble(ulong hash, double value)
    {
        if (_setInputEventDouble == null || _handle == IntPtr.Zero) return false;

        var buffer = new[] { value };
        var hr = _setInputEventDouble(_handle, hash, (uint)sizeof(double), buffer);
        if (hr != 0) Log.Warn($"SetInputEventD(0x{hash:X16}) hr=0x{hr:X8}");
        return hr == 0;
    }

    /// <summary>
    /// Asks the sim to enumerate an input event's parameters. The response carries a text
    /// value, which tells us whether the event is parameter-driven rather than numeric.
    /// </summary>
    public void EnumerateInputEventParams(ulong hash)
    {
        if (_enumerateInputEventParams == null || _handle == IntPtr.Zero) return;
        var hr = _enumerateInputEventParams(_handle, hash);
        Log.Debug($"EnumerateInputEventParams(0x{hash:X16}) -> hr=0x{hr:X8}");
    }

    /// <summary>
    /// Writes an INT32 data definition value to the user's sim object. Some cockpit state is a
    /// writable SimVar rather than an event — the transponder mode is one.
    /// </summary>
    public bool SetDataInt32(uint defineId, int value)
    {
        if (_setDataOnSimObject == null || _handle == IntPtr.Zero) return false;

        var buffer = new[] { value };
        var hr = _setDataOnSimObject(_handle, defineId, ObjectIdUser, 0, 1, (uint)sizeof(int), buffer);
        if (hr != 0) Log.Warn($"SetDataOnSimObject(define {defineId}) hr=0x{hr:X8}");
        return hr == 0;
    }

    /// <summary>Registers interest in an input event.</summary>
    public bool SubscribeInputEvent(ulong hash)
    {
        if (_subscribeInputEvent == null || _handle == IntPtr.Zero) return false;
        var hr = _subscribeInputEvent(_handle, hash);
        Log.Debug($"SubscribeInputEvent(0x{hash:X16}) -> hr=0x{hr:X8}");
        return hr == 0;
    }

    private void OnDispatch(IntPtr pData, uint cbData, IntPtr context)
    {
        try
        {
            if (pData == IntPtr.Zero) return;

            var recvId = (uint)Marshal.ReadInt32(pData, 8);

            if (RawRecv != null && cbData > 0 && cbData < 4 * 1024 * 1024)
            {
                var raw = new byte[cbData];
                Marshal.Copy(pData, raw, 0, (int)cbData);
                try { RawRecv(recvId, raw); } catch { /* diagnostics must not throw */ }
            }

            if (recvId != RecvIdSimObjectData) return;

            var defineId = (uint)Marshal.ReadInt32(pData, 20);
            const int dataOffset = 40;

            // Use the reported size: definitions mix FLOAT64 and INT32 members, so the
            // payload length cannot be derived from the entry count alone.
            var payloadLength = (int)cbData - dataOffset;
            if (payloadLength <= 0) return;

            var payload = new byte[payloadLength];
            Marshal.Copy(pData + dataOffset, payload, 0, payloadLength);

            SimObjectData?.Invoke(defineId, payload);
        }
        catch (Exception ex)
        {
            Log.Error("dispatch callback failed", ex);
        }
    }

    private T Bind<T>(string export) where T : Delegate
    {
        if (!NativeLibrary.TryGetExport(_module, export, out var address) || address == IntPtr.Zero)
            throw new MissingMethodException($"export {export} not found");
        return Marshal.GetDelegateForFunctionPointer<T>(address);
    }

    private T? TryBind<T>(string export) where T : Delegate
    {
        if (!NativeLibrary.TryGetExport(_module, export, out var address) || address == IntPtr.Zero) return null;
        return Marshal.GetDelegateForFunctionPointer<T>(address);
    }

    private void Free()
    {
        if (_module == IntPtr.Zero) return;
        try { NativeLibrary.Free(_module); } catch { /* nothing useful to do */ }
        _module = IntPtr.Zero;
        ClientPath = "";
    }

    public void Dispose()
    {
        Close();
        Free();
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate void DispatchCallback(IntPtr pData, uint cbData, IntPtr context);

    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Ansi, BestFitMapping = false)]
    private delegate int OpenDelegate(out IntPtr handle, string name, IntPtr hWnd, uint userEventWin32, IntPtr hEventHandle, uint configIndex);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CloseDelegate(IntPtr handle);

    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Ansi, BestFitMapping = false)]
    private delegate int MapDelegate(IntPtr handle, uint eventId, string eventName);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int TransmitDelegate(IntPtr handle, uint objectId, uint eventId, uint data, uint groupId, uint flags);

    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Ansi, BestFitMapping = false)]
    private delegate int AddToDefinitionDelegate(IntPtr handle, uint defineId, string datumName, string unitsName, uint datatype, float epsilon, uint datumId);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int RequestDataDelegate(IntPtr handle, uint requestId, uint defineId, uint objectId, uint period, uint flags, uint origin, uint interval, uint limit);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CallDispatchDelegate(IntPtr handle, DispatchCallback callback, IntPtr context);

    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Ansi)]
    private delegate int GetLastErrorDelegate(IntPtr handle, StringBuilder buffer, uint length);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int EnumerateInputEventsDelegate(IntPtr handle, uint requestId);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetInputEventDelegate(IntPtr handle, ulong hash, uint cbUnitSize, float[] value);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetInputEventDoubleDelegate(IntPtr handle, ulong hash, uint cbUnitSize, double[] value);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SubscribeInputEventDelegate(IntPtr handle, ulong hash);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int EnumerateInputEventParamsDelegate(IntPtr handle, ulong hash);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetDataOnSimObjectDelegate(
        IntPtr handle, uint defineId, uint objectId, uint flags, uint arrayCount, uint cbUnitSize, int[] data);
}
