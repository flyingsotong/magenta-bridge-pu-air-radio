using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using MagentaBridge.Core;

namespace MagentaBridge.Native;

public sealed class HidDeviceInfo
{
    public string Path { get; init; } = "";
    public ushort VendorId { get; init; }
    public ushort ProductId { get; init; }
    public ushort VersionNumber { get; init; }
    public string Serial { get; init; } = "";
    public string Product { get; init; } = "";
    public string Manufacturer { get; init; } = "";
    public int InputReportLength { get; init; }
    public int OutputReportLength { get; init; }

    public string Display => string.IsNullOrWhiteSpace(Product)
        ? $"VID_{VendorId:X4}&PID_{ProductId:X4}"
        : $"{Product} ({VendorId:X4}:{ProductId:X4}{(string.IsNullOrWhiteSpace(Serial) ? "" : $" sn {Serial}")})";
}

/// <summary>
/// Win32 HID access. No hidapi.dll dependency, no third-party licence surface.
/// The PU Radio exposes one HID interface (usage page 1, usage 4, report id 0x14,
/// 5-byte reports, 14 buttons in the first two data bytes) plus a USB serial port.
/// </summary>
public static class Hid
{
    private static Guid HidInterfaceGuid = new("4D1E55B2-F16F-11CF-88CB-001111000030");

    public static List<HidDeviceInfo> Enumerate(ushort vendorId, ushort productId)
    {
        var results = new List<HidDeviceInfo>();

        var deviceInfoSet = Win32.SetupDiGetClassDevs(ref HidInterfaceGuid, IntPtr.Zero, IntPtr.Zero,
            Win32.DigcfPresent | Win32.DigcfDeviceInterface);
        if (deviceInfoSet == IntPtr.Zero || deviceInfoSet == new IntPtr(-1)) return results;

        try
        {
            var interfaceData = new Win32.SpDeviceInterfaceData();
            interfaceData.cbSize = Marshal.SizeOf(interfaceData);

            for (uint index = 0; ; index++)
            {
                if (!Win32.SetupDiEnumDeviceInterfaces(deviceInfoSet, IntPtr.Zero, ref HidInterfaceGuid, index, ref interfaceData))
                    break;

                var path = GetDevicePath(deviceInfoSet, ref interfaceData);
                if (string.IsNullOrEmpty(path)) continue;

                var info = ReadInfo(path);
                if (info == null) continue;
                if (info.VendorId != vendorId || info.ProductId != productId) continue;

                results.Add(info);
            }
        }
        finally
        {
            Win32.SetupDiDestroyDeviceInfoList(deviceInfoSet);
        }

        return results;
    }

    private static string GetDevicePath(IntPtr set, ref Win32.SpDeviceInterfaceData interfaceData)
    {
        Win32.SetupDiGetDeviceInterfaceDetail(set, ref interfaceData, IntPtr.Zero, 0, out var required, IntPtr.Zero);
        if (required <= 0) return "";

        var buffer = Marshal.AllocHGlobal((int)required);
        try
        {
            // cbSize is 8 on x64 (4-byte size plus a 2-byte char plus padding).
            Marshal.WriteInt32(buffer, IntPtr.Size == 8 ? 8 : 6);

            if (!Win32.SetupDiGetDeviceInterfaceDetail(set, ref interfaceData, buffer, required, out _, IntPtr.Zero))
                return "";

            return Marshal.PtrToStringUni(buffer + 4) ?? "";
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static HidDeviceInfo? ReadInfo(string path)
    {
        using var handle = Win32.CreateFile(path, Win32.GenericRead | Win32.GenericWrite,
            Win32.FileShareRead | Win32.FileShareWrite, IntPtr.Zero, Win32.OpenExisting, 0, IntPtr.Zero);

        if (handle.IsInvalid) return null;

        var attributes = new Win32.HidAttributes { Size = Marshal.SizeOf<Win32.HidAttributes>() };
        if (!Win32.HidD_GetAttributes(handle, ref attributes)) return null;

        var info = new HidDeviceInfo
        {
            Path = path,
            VendorId = attributes.VendorID,
            ProductId = attributes.ProductID,
            VersionNumber = attributes.VersionNumber,
            Serial = ReadString(handle, Win32.HidD_GetSerialNumberString),
            Product = ReadString(handle, Win32.HidD_GetProductString),
            Manufacturer = ReadString(handle, Win32.HidD_GetManufacturerString),
        };

        if (Win32.HidD_GetPreparsedData(handle, out var preparsed) && preparsed != IntPtr.Zero)
        {
            try
            {
                var caps = new Win32.HidpCaps { Reserved = new ushort[17] };
                if (Win32.HidP_GetCaps(preparsed, ref caps) == Win32.HidpStatusSuccess)
                {
                    return new HidDeviceInfo
                    {
                        Path = info.Path,
                        VendorId = info.VendorId,
                        ProductId = info.ProductId,
                        VersionNumber = info.VersionNumber,
                        Serial = info.Serial,
                        Product = info.Product,
                        Manufacturer = info.Manufacturer,
                        InputReportLength = caps.InputReportByteLength,
                        OutputReportLength = caps.OutputReportByteLength,
                    };
                }
            }
            finally
            {
                Win32.HidD_FreePreparsedData(preparsed);
            }
        }

        return info;
    }

    private static string ReadString(SafeFileHandle handle, Func<SafeFileHandle, StringBuilder, uint, bool> reader)
    {
        try
        {
            var buffer = new StringBuilder(256);
            return reader(handle, buffer, (uint)buffer.Capacity) ? buffer.ToString() : "";
        }
        catch
        {
            return "";
        }
    }

    /// <summary>Returns the first unit found. Two radios on one PC is not a supported case.</summary>
    public static HidDeviceInfo? Find(int vendorId, int productId)
    {
        var devices = Enumerate((ushort)vendorId, (ushort)productId);
        if (devices.Count == 0) return null;

        if (devices.Count > 1)
            Log.Warn($"{devices.Count} PU Radio units present; binding the first");

        return devices[0];
    }
}

/// <summary>An open HID handle with a dedicated overlapped read loop.</summary>
public sealed class HidConnection : IDisposable
{
    private readonly SafeFileHandle _handle;
    private readonly int _inputLength;
    private volatile bool _stopping;
    private Thread? _reader;

    public HidDeviceInfo Info { get; }

    /// <summary>Raised on the reader thread for each input report.</summary>
    public event Action<byte[]>? ReportReceived;
    public event Action<string>? Disconnected;

    public HidConnection(HidDeviceInfo info)
    {
        Info = info;
        _inputLength = Math.Max(info.InputReportLength, 8);

        _handle = Win32.CreateFile(info.Path, Win32.GenericRead | Win32.GenericWrite,
            Win32.FileShareRead | Win32.FileShareWrite, IntPtr.Zero, Win32.OpenExisting,
            Win32.FileFlagOverlapped, IntPtr.Zero);

        if (_handle.IsInvalid)
            throw new InvalidOperationException(
                $"could not open {info.Display} (win32 error {Marshal.GetLastWin32Error()})");
    }

    public void Start()
    {
        if (_reader != null) return;
        _reader = new Thread(ReadLoop) { IsBackground = true, Name = "HID reader" };
        _reader.Start();
    }

    private void ReadLoop()
    {
        var buffer = new byte[_inputLength];
        var readEvent = Win32.CreateEvent(IntPtr.Zero, true, false, null);
        var overlapped = new NativeOverlapped { EventHandle = readEvent };

        try
        {
            while (!_stopping)
            {
                Win32.ResetEvent(readEvent);

                var ok = Win32.ReadFile(_handle, buffer, (uint)buffer.Length, out var bytesRead, ref overlapped);

                if (!ok)
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error != Win32.ErrorIoPending)
                    {
                        if (!_stopping)
                        {
                            Log.Warn($"HID read failed (win32 error {error})");
                            Disconnected?.Invoke($"read failed (win32 error {error})");
                        }
                        return;
                    }

                    // Wait for THIS request to complete. Re-issuing ReadFile while it is still
                    // outstanding leaks a pending read on every timeout and loses reports.
                    var completed = false;
                    while (!_stopping)
                    {
                        var wait = Win32.WaitForSingleObject(readEvent, 100);
                        if (wait == Win32.WaitObject0) { completed = true; break; }

                        if (wait != Win32.WaitTimeout)
                        {
                            if (!_stopping) Disconnected?.Invoke("read wait failed");
                            return;
                        }
                    }

                    if (!completed) return;   // stopping

                    if (!Win32.GetOverlappedResult(_handle, ref overlapped, out bytesRead, false))
                    {
                        if (!_stopping)
                        {
                            var err = Marshal.GetLastWin32Error();
                            Log.Warn($"HID overlapped read failed (win32 error {err})");
                            Disconnected?.Invoke($"read failed (win32 error {err})");
                        }
                        return;
                    }
                }

                if (bytesRead > 0 && !_stopping)
                {
                    var report = new byte[bytesRead];
                    Array.Copy(buffer, report, (int)bytesRead);
                    try
                    {
                        ReportReceived?.Invoke(report);
                    }
                    catch (Exception ex)
                    {
                        Log.Error("report handler failed", ex);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            if (!_stopping) Log.Error("HID reader crashed", ex);
        }
        finally
        {
            Win32.CloseHandle(readEvent);
        }
    }

    public void Dispose()
    {
        // Closing the handle cancels the outstanding read; the reader thread is a daemon,
        // so a blocked ReadFile cannot keep the process alive.
        _stopping = true;
        try { _handle?.Dispose(); } catch { /* ignore */ }
        try { _reader?.Join(1000); } catch { /* ignore */ }
        _reader = null;
    }
}
