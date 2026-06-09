using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace BetopToXInput.Core;

/// <summary>
/// Sends force-feedback / rumble reports to the Betop C031 via HID output report.
///
/// Reverse-engineered from <c>sub_1003070</c> in GaJoyFF.dll:
///   5 bytes: [ReportID, 0, 0, strongMotor, weakMotor]
///   - strongMotor: 0..0x7F (127). Values &lt; 0x18 (24) are treated as zero by the
///     DLL (dead-zone), so we clamp them to 0 before sending.
///   - weakMotor:   same range / dead-zone.
///
/// IMPORTANT: This writer does NOT call <c>CreateFile</c> with
/// <c>GENERIC_WRITE</c>.  A non-elevated process opening a HID device with
/// <c>GENERIC_WRITE</c> almost always gets ERROR_ACCESS_DENIED on the
/// standard HID class driver.  Instead we open with 0 (query) access and use
/// <c>HidD_SetOutputReport</c>, which is the supported way for user-mode
/// code to send HID output reports without admin privileges.
/// </summary>
public sealed class BetopRumbleWriter : IDisposable
{
    public const byte OutputReportId = 0x05;
    public const byte DeadZone = 0x18;
    public const byte MaxMagnitude = 0x7F;

    private IntPtr _deviceHandle = new(-1);
    private readonly object _writeLock = new();
    private long _lastRumbleLogMs;

    public BetopRumbleWriter()
    {
        var paths = HidPInvoke.EnumerateHidPaths(
            BetopHidReader.BetopVendorId,
            BetopHidReader.BetopProductId);

        if (paths.Count == 0)
        {
            throw new InvalidOperationException(
                "Betop C031 not found when opening rumble writer.");
        }

        Exception? lastError = null;
        foreach (var path in paths)
        {
            // Open with ZERO access — HidD_SetOutputReport works on this.
            IntPtr h = HidPInvoke.OpenDevice(path, read: false, write: false);
            if (h == IntPtr.Zero || h == new IntPtr(-1))
            {
                lastError = Marshal.GetExceptionForHR(Marshal.GetHRForLastWin32Error())
                            ?? new InvalidOperationException("CreateFileW returned INVALID_HANDLE_VALUE");
                continue;
            }

            // Sanity check: the device's VID/PID attributes must still match
            // (some enumerators return paths that share the same parent).
            var attrs = new HidPInvoke.HidAttributes();
            attrs.Size = Marshal.SizeOf(attrs);
            if (!HidPInvoke.HidD_GetAttributes(h, ref attrs) ||
                attrs.VendorID != BetopHidReader.BetopVendorId ||
                attrs.ProductID != BetopHidReader.BetopProductId)
            {
                HidPInvoke.CloseHandle(h);
                continue;
            }

            _deviceHandle = h;
            break;
        }

        if (_deviceHandle == IntPtr.Zero || _deviceHandle == new IntPtr(-1))
        {
            throw new InvalidOperationException(
                "Betop C031 was enumerated but no write-capable HID path could be opened. " +
                "The device may be in exclusive use by another process, or the unsigned " +
                "Betop driver is loaded and not releasing the device. Underlying error: " +
                (lastError?.Message ?? "(unknown)"));
        }
    }

    /// <summary>
    /// Send a rumble command. Inputs are normalised 0..1 (Xbox 360 scale).
    /// </summary>
    public bool SetRumble(double strong01, double weak01)
    {
        if (_deviceHandle == IntPtr.Zero || _deviceHandle == new IntPtr(-1))
            return false;

        byte strong = NormalizeMotor(strong01);
        byte weak   = NormalizeMotor(weak01);

        // Try two report ID conventions.  GaJoyFF.dll was observed sending
        // [0x05, 0, 0, strong, weak] (report ID = 0x05), but some firmwares
        // expect the first byte to be 0x00 ("no report ID") and shift the
        // motor bytes left by one.  We try 0x05 first, fall back to 0x00.
        bool ok = TryOutputReport(0x05, strong, weak) ||
                  TryOutputReport(0x00, strong, weak);

        return ok;
    }

    private bool TryOutputReport(byte reportId, byte strong, byte weak)
    {
        var buffer = new byte[5]
        {
            reportId,
            0x00,
            0x00,
            strong,
            weak
        };

        lock (_writeLock)
        {
            bool sent = HidPInvoke.HidD_SetOutputReport(_deviceHandle, buffer, (uint)buffer.Length);
            int err = Marshal.GetLastWin32Error();
            if (sent)
            {
                // Throttle success log so the console doesn't explode when
                // the game is constantly ratcheting the motor levels.
                long now = Environment.TickCount64;
                if (now - _lastRumbleLogMs > 1000)
                {
                    _lastRumbleLogMs = now;
                    Console.Out.WriteLine($"[rumble] OK id=0x{reportId:X2} strong={strong} weak={weak}");
                }
            }
            else
            {
                Console.Error.WriteLine(
                    $"[rumble] HidD_SetOutputReport failed (id=0x{reportId:X2} strong={strong} weak={weak}) " +
                    $"GetLastError={err} (0x{err:X8}) — {Marshal.GetExceptionForHR(Marshal.GetHRForLastWin32Error())?.Message ?? "?"}");
            }
            return sent;
        }
    }

    public void Stop() => SetRumble(0, 0);

    private static byte NormalizeMotor(double v)
    {
        if (v <= 0.0) return 0;
        if (v >= 1.0) return MaxMagnitude;
        byte raw = (byte)(v * MaxMagnitude);
        if (raw < DeadZone) return 0;
        return raw;
    }

    public void Dispose()
    {
        try
        {
            Stop();
            if (_deviceHandle != IntPtr.Zero && _deviceHandle != new IntPtr(-1))
            {
                HidPInvoke.CloseHandle(_deviceHandle);
            }
        }
        catch { /* ignore */ }
        _deviceHandle = IntPtr.Zero;
    }
}
