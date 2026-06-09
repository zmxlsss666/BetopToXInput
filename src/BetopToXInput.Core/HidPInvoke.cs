using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace BetopToXInput.Core;

/// <summary>
/// Pure P/Invoke wrapper for the Windows HID class driver. The goal is to read
/// the Betop C031's HID input reports (and write the rumble output report)
/// without relying on the unsigned Betop vendor driver.
///
/// Only one static entry-point is exposed: <see cref="OpenByVidPid"/> returns a
/// raw OS handle. Read/write are then a single <c>ReadFile</c> / <c>WriteFile</c>.
/// </summary>
internal static class HidPInvoke
{
    // Standard Windows HID Class Device Interface GUID
    // {4d1e55b2-f16f-11cf-88cb-001111000030}
    public static readonly Guid HidClassGuid = new(
        0x4d1e55b2, 0xf16f, 0x11cf, 0x88, 0xcb, 0x00, 0x11, 0x11, 0x00, 0x00, 0x30);

    // Public-facing wrapper hooks. The core methods stay internal so the
    // Win32 surface is contained; Program.cs and any future tools call into
    // the BetopHidPInvoke façade in the Core namespace.
    public static List<string> EnumerateHidPathsPublic(ushort vendorId, ushort productId)
        => EnumerateHidPaths(vendorId, productId);

    public static IntPtr OpenDevicePublic(string path, bool read, bool write)
        => OpenDevice(path, read, write);

    [StructLayout(LayoutKind.Sequential)]
    public struct SpDeviceInterfaceData
    {
        public int CbSize;
        public Guid InterfaceClassGuid;
        public int Flags;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SpDeviceInterfaceDetailData
    {
        public int CbSize;
        // Path is a variable-length Unicode string following this struct.
        // We allocate 512 bytes and rely on MAX_PATH-style truncation.
        public char DevicePath;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct HidAttributes
    {
        public int Size;
        public ushort VendorID;
        public ushort ProductID;
        public ushort VersionNumber;
        public ushort Reserved;
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr SetupDiGetClassDevs(
        ref Guid classGuid,
        IntPtr enumerator,
        IntPtr hwndParent,
        int flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    public static extern bool SetupDiEnumDeviceInterfaces(
        IntPtr deviceInfoSet,
        IntPtr deviceInfoData,
        ref Guid interfaceClassGuid,
        int memberIndex,
        ref SpDeviceInterfaceData deviceInterfaceData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool SetupDiGetDeviceInterfaceDetail(
        IntPtr deviceInfoSet,
        ref SpDeviceInterfaceData deviceInterfaceData,
        IntPtr deviceInterfaceDetailData, // optional
        int deviceInterfaceDetailDataSize,
        out int requiredSize,
        IntPtr deviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    public static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    public const int DIGCF_PRESENT = 0x00000002;
    public const int DIGCF_DEVICEINTERFACE = 0x00000010;
    public const int ERROR_NO_MORE_ITEMS = 259;
    public const int ERROR_INSUFFICIENT_BUFFER = 122;

    [DllImport("hid.dll", SetLastError = true)]
    public static extern bool HidD_GetAttributes(
        IntPtr hidDeviceObject,
        ref HidAttributes attributes);

    /// <summary>
    /// Send an output report to the HID device.  Unlike <c>WriteFile</c>, this
    /// works on a handle opened with 0 (query) access — that's the only kind of
    /// handle a non-elevated user-mode process can usually get on a HID device
    /// enumerated by the standard HID class driver.  The first byte of
    /// <paramref name="buffer"/> is the report ID (use 0 if the device does not
    /// use report IDs).
    /// </summary>
    [DllImport("hid.dll", SetLastError = true)]
    public static extern bool HidD_SetOutputReport(
        IntPtr hidDeviceObject,
        byte[] buffer,
        uint bufferLength);

    /// <summary>
    /// Get the preparsed data blob for a HID device.  Use it with
    /// <c>HidP_GetCaps</c> to determine input/output report lengths etc.
    /// </summary>
    [DllImport("hid.dll", SetLastError = true)]
    public static extern IntPtr HidD_GetPreparsedData(
        IntPtr hidDeviceObject,
        out IntPtr preparsedData);

    [DllImport("hid.dll", SetLastError = true)]
    public static extern bool HidD_FreePreparsedData(IntPtr preparsedData);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool ReadFile(
        IntPtr hFile,
        byte[] lpBuffer,
        uint nNumberOfBytesToRead,
        out uint lpNumberOfBytesRead,
        IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool WriteFile(
        IntPtr hFile,
        byte[] lpBuffer,
        uint nNumberOfBytesToWrite,
        out uint lpNumberOfBytesWritten,
        IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(IntPtr hObject);

    public const uint GENERIC_READ = 0x80000000;
    public const uint GENERIC_WRITE = 0x40000000;
    public const uint FILE_SHARE_READ = 0x00000001;
    public const uint FILE_SHARE_WRITE = 0x00000002;
    public const uint OPEN_EXISTING = 3;

    /// <summary>
    /// Enumerate HID device paths whose hardware ID matches the given VID/PID.
    /// We do NOT open the device here - we parse VID/PID straight from the
    /// path string (e.g. <c>\\?\HID#VID_0E8F&amp;PID_0003#6&amp;a81bb9&amp;0&amp;0000#{...}</c>).
    /// This avoids touching the device during enumeration, which is important
    /// because some HID stacks hang on CreateFileW when the device is in
    /// exclusive use or when the Betop vendor driver is misbehaving.
    /// </summary>
    public static List<string> EnumerateHidPaths(ushort vendorId, ushort productId)
    {
        var result = new List<string>();
        var classGuid = HidClassGuid;
        IntPtr info = SetupDiGetClassDevs(
            ref classGuid,
            IntPtr.Zero,
            IntPtr.Zero,
            DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
        if (info == IntPtr.Zero || info == new IntPtr(-1))
            return result;

        string vidHex = vendorId.ToString("X4");
        string pidHex = productId.ToString("X4");
        // Match VID_0E8F&PID_0003 in either order (some firmwares emit them swapped).
        string pattern = $@"VID_{vidHex}&PID_{pidHex}|PID_{pidHex}&VID_{vidHex}";

        try
        {
            int index = 0;
            while (true)
            {
                var ifData = new SpDeviceInterfaceData { CbSize = Marshal.SizeOf<SpDeviceInterfaceData>() };
                if (!SetupDiEnumDeviceInterfaces(info, IntPtr.Zero, ref classGuid, index, ref ifData))
                {
                    int err = Marshal.GetLastWin32Error();
                    if (err == ERROR_NO_MORE_ITEMS) break;
                    index++;
                    continue;
                }

                int required = 0;
                SetupDiGetDeviceInterfaceDetail(info, ref ifData, IntPtr.Zero, 0, out required, IntPtr.Zero);
                if (required <= 0)
                {
                    index++;
                    continue;
                }

                IntPtr detail = Marshal.AllocHGlobal(required);
                try
                {
                    // cbSize is at offset 0 (4 bytes). The DevicePath WCHAR
                    // array starts immediately after — offset 4 — regardless
                    // of the padding the struct has on 64-bit.
                    Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                    if (SetupDiGetDeviceInterfaceDetail(info, ref ifData, detail, required, out required, IntPtr.Zero))
                    {
                        string path = Marshal.PtrToStringUni(detail + 4)!;
                        if (path != null &&
                            path.IndexOf($"VID_{vidHex}", StringComparison.OrdinalIgnoreCase) >= 0 &&
                            path.IndexOf($"PID_{pidHex}", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            result.Add(path);
                        }
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(detail);
                }
                index++;
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(info);
        }
        return result;
    }

    /// <summary>
    /// Enumerate ALL HID device interface paths (no VID/PID filter). Useful as
    /// a diagnostic when the filtered enumeration returns nothing.
    /// </summary>
    public static List<string> EnumerateAllHidPaths()
    {
        var result = new List<string>();
        var classGuid = HidClassGuid;
        IntPtr info = SetupDiGetClassDevs(
            ref classGuid,
            IntPtr.Zero,
            IntPtr.Zero,
            DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
        if (info == IntPtr.Zero || info == new IntPtr(-1))
            return result;

        try
        {
            int index = 0;
            while (true)
            {
                var ifData = new SpDeviceInterfaceData { CbSize = Marshal.SizeOf<SpDeviceInterfaceData>() };
                if (!SetupDiEnumDeviceInterfaces(info, IntPtr.Zero, ref classGuid, index, ref ifData))
                {
                    int err = Marshal.GetLastWin32Error();
                    if (err == ERROR_NO_MORE_ITEMS) break;
                    index++;
                    continue;
                }

                int required = 0;
                SetupDiGetDeviceInterfaceDetail(info, ref ifData, IntPtr.Zero, 0, out required, IntPtr.Zero);
                if (required <= 0) { index++; continue; }

                IntPtr detail = Marshal.AllocHGlobal(required);
                try
                {
                    Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                    if (SetupDiGetDeviceInterfaceDetail(info, ref ifData, detail, required, out required, IntPtr.Zero))
                    {
                        string path = Marshal.PtrToStringUni(detail + 4)!;
                        if (path != null) result.Add(path);
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(detail);
                }
                index++;
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(info);
        }
        return result;
    }

    /// <summary>
    /// Open a HID device by VID/PID. Returns INVALID_HANDLE_VALUE on failure.
    /// </summary>
    public static IntPtr OpenByVidPid(ushort vendorId, ushort productId, bool read, bool write)
    {
        var paths = EnumerateHidPaths(vendorId, productId);
        if (paths.Count == 0)
            return new IntPtr(-1);

        return OpenDevice(paths[0], read, write);
    }

    /// <summary>
    /// Open a specific HID device path. Returns INVALID_HANDLE_VALUE on failure.
    /// </summary>
    public static IntPtr OpenDevice(string path, bool read, bool write)
    {
        uint access = 0;
        if (read) access |= GENERIC_READ;
        if (write) access |= GENERIC_WRITE;

        return CreateFileW(
            path, access, FILE_SHARE_READ | FILE_SHARE_WRITE,
            IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
    }
}
