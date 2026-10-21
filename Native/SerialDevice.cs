using System.IO.Ports;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using MagentaBridge.Core;

namespace MagentaBridge.Native;

public sealed record SerialPortInfo(string PortName, string InstanceId, string Description);

/// <summary>
/// Finds the COM port that belongs to the radio's USB device. The radio is a composite
/// device: interface 00 is the HID joystick, interface 01 is a CDC serial port that drives
/// the displays. Enumerating by VID/PID is what makes the port number irrelevant — Windows
/// can assign a different COM number after a replug and this still binds.
/// </summary>
public static class SerialPortFinder
{
    private static Guid PortsClass = new("4d36e978-e325-11ce-bfc1-08002be10318");

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDevInfoData
    {
        public int cbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, IntPtr enumerator, IntPtr parent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInfo(IntPtr deviceInfoSet, uint memberIndex, ref SpDevInfoData deviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool SetupDiGetDeviceInstanceId(
        IntPtr deviceInfoSet, ref SpDevInfoData deviceInfoData, StringBuilder deviceInstanceId,
        int deviceInstanceIdSize, out int requiredSize);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool SetupDiGetDeviceRegistryProperty(
        IntPtr deviceInfoSet, ref SpDevInfoData deviceInfoData, uint property,
        out uint propertyRegDataType, byte[]? propertyBuffer, uint propertyBufferSize, out uint requiredSize);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    private const uint DigcfPresent = 0x00000002;
    private const uint SpdrpFriendlyName = 0x0000000C;

    public static SerialPortInfo? Find(int vendorId, int productId)
    {
        var tag = $"VID_{vendorId:X4}&PID_{productId:X4}";
        IntPtr set = SetupDiGetClassDevs(ref PortsClass, IntPtr.Zero, IntPtr.Zero, DigcfPresent);
        if (set == IntPtr.Zero || set == new IntPtr(-1)) return null;

        try
        {
            var data = new SpDevInfoData { cbSize = Marshal.SizeOf<SpDevInfoData>() };

            for (uint index = 0; SetupDiEnumDeviceInfo(set, index, ref data); index++)
            {
                var id = new StringBuilder(512);
                if (!SetupDiGetDeviceInstanceId(set, ref data, id, id.Capacity, out _)) continue;

                var instanceId = id.ToString();
                if (!instanceId.Contains(tag, StringComparison.OrdinalIgnoreCase)) continue;

                var friendly = ReadProperty(set, ref data, SpdrpFriendlyName);

                // The port name lives on the device's own registry node and survives a
                // user-renamed friendly name, so prefer it over parsing "(COMxx)".
                var portName = ReadPortNameFromRegistry(instanceId);
                if (string.IsNullOrEmpty(portName))
                {
                    var match = System.Text.RegularExpressions.Regex.Match(friendly, @"\(COM(\d+)\)");
                    if (match.Success) portName = "COM" + match.Groups[1].Value;
                }

                if (string.IsNullOrEmpty(portName)) continue;
                return new SerialPortInfo(portName, instanceId, friendly);
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }

        return null;
    }

    private static string ReadProperty(IntPtr set, ref SpDevInfoData data, uint property)
    {
        var buffer = new byte[1024];
        if (!SetupDiGetDeviceRegistryProperty(set, ref data, property, out _, buffer, (uint)buffer.Length, out var size))
            return "";

        var length = (int)Math.Min(size, (uint)buffer.Length);
        return Encoding.Unicode.GetString(buffer, 0, length).TrimEnd('\0');
    }

    private static string ReadPortNameFromRegistry(string instanceId)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                $@"SYSTEM\CurrentControlSet\Enum\{instanceId}\Device Parameters");
            return key?.GetValue("PortName") as string ?? "";
        }
        catch (Exception ex)
        {
            Log.Debug($"could not read PortName for {instanceId}: {ex.Message}");
            return "";
        }
    }
}

/// <summary>
/// The write-only side of the panel: a 9600 8N1 line carrying one text frame. The firmware
/// keeps displaying the last frame while the port is open and falls back to its own demo
/// pattern shortly after the port closes, so the link stays open for the life of the app.
/// </summary>
public sealed class SerialLink : IDisposable
{
    private SerialPort? _port;

    public string PortName { get; private set; } = "";
    public bool IsOpen => _port?.IsOpen == true;

    /// <summary>Opens the port. Throws with a readable reason on failure; the caller decides
    /// whether that is "another app has it" or a transient error.</summary>
    public void Open(string portName)
    {
        Close();

        var port = new SerialPort(portName, 9600, Parity.None, 8, StopBits.One)
        {
            Handshake = Handshake.None,
            DtrEnable = false,
            RtsEnable = false,
            WriteTimeout = 500,
            ReadTimeout = 500,
            WriteBufferSize = 1024,
        };

        port.Open();
        _port = port;
        PortName = portName;
        Log.Info($"radio display link open on {portName} at 9600 8N1");
    }

    /// <summary>Writes one frame. Returns false when the port has gone away.</summary>
    public bool Write(string frame)
    {
        var port = _port;
        if (port == null || !port.IsOpen) return false;

        try
        {
            port.Write(frame);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"serial write failed: {ex.Message}");
            return false;
        }
    }

    public void Close()
    {
        var port = _port;
        _port = null;
        if (port == null) return;

        try
        {
            if (port.IsOpen) port.Close();
        }
        catch { /* the device may already be gone */ }

        try { port.Dispose(); } catch { /* ignore */ }

        if (PortName.Length > 0) Log.Info($"radio display link closed ({PortName})");
    }

    public void Dispose() => Close();
}
