using System;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using BetopToXInput.Core;

namespace BetopToXInput.Proxy;

/// <summary>
/// User-mode xinput1_3.dll proxy. The DLL is named <c>BetopToXInput.Proxy.dll</c>
/// and is renamed to <c>xinput1_3.dll</c> / <c>xinput1_4.dll</c> in the game's
/// directory (or, more commonly, dropped into the same folder as the game exe
/// and the game's own copy of xinput is shimmed by file replacement).
///
/// All public exports are 1:1 forwarders to the real <c>xinput1_3.dll</c>,
/// except for <see cref="XInputGetState"/> and <see cref="XInputSetState"/>
/// which read/write a shared-memory slot owned by <c>BetopToXInput.App</c>.
///
/// This means the game still believes it's talking to a real Xbox 360 driver,
/// but every query/command is reflected from the Betop gamepad we are watching.
///
/// IMPORTANT: this proxy is loaded INSIDE the game process. The HID hardware
/// is held by the long-running service. The two communicate only through the
/// shared memory file defined in <see cref="XInputSharedMemory"/>.
/// </summary>
public static class XInputProxy
{
    private const string RealXInputDll = "xinput1_3.dll";

    // The shared file is created on first call. Access is lock-free; the
    // generation counters in the header prevent torn reads.
    private static readonly MemoryMappedFile SharedFile =
        MemoryMappedFile.CreateOrOpen(XInputSharedMemory.MapName, XInputSharedMemory.TotalSize);

    private static readonly MemoryMappedViewAccessor View = SharedFile.CreateViewAccessor();
    private static uint _lastSeenInputGen;
    private static XInputGamepad _cachedPad;
    private static uint _lastSeenOutputGen;
    private static (byte Large, byte Small) _cachedMotors;

    // ----- Real XInput1_3 thunks (forward to original DLL via LoadLibrary) -----
    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibraryW(string lpFileName);

    [DllImport("kernel32", SetLastError = true)]
    private static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);

    private static readonly IntPtr RealDll = LoadLibraryW(RealXInputDll);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int XInputGetStateDelegate(uint dwUserIndex, out XInputState pState);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int XInputSetStateDelegate(uint dwUserIndex, ref XInputVibration pVibration);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int XInputGetCapabilitiesDelegate(uint dwUserIndex, uint dwFlags, out XInputCapabilities pCaps);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void XInputEnableDelegate(bool enable);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint XInputGetBatteryTypeDelegate(uint dwUserIndex, byte devType, out XInputBatteryInfo info);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint XInputGetKeystrokeDelegate(uint dwUserIndex, uint dwReserved, out XInputKeystroke pStroke);

    private static readonly XInputGetStateDelegate? RealGetState = Resolve<XInputGetStateDelegate>("XInputGetState");
    private static readonly XInputSetStateDelegate? RealSetState = Resolve<XInputSetStateDelegate>("XInputSetState");
    private static readonly XInputGetCapabilitiesDelegate? RealGetCaps = Resolve<XInputGetCapabilitiesDelegate>("XInputGetCapabilities");
    private static readonly XInputEnableDelegate? RealEnable = Resolve<XInputEnableDelegate>("XInputEnable");
    private static readonly XInputGetBatteryTypeDelegate? RealGetBattery = Resolve<XInputGetBatteryTypeDelegate>("XInputGetBatteryType");
    private static readonly XInputGetKeystrokeDelegate? RealGetKeystroke = Resolve<XInputGetKeystrokeDelegate>("XInputGetKeystroke");

    private static T? Resolve<T>(string name) where T : Delegate
    {
        if (RealDll == IntPtr.Zero) return null;
        var addr = GetProcAddress(RealDll, name);
        return addr == IntPtr.Zero ? null : Marshal.GetDelegateForFunctionPointer<T>(addr);
    }

    // ----- Public XInput API surface -----
    // The DllExport signatures match the XInput 1.3 / 1.4 ABI byte-for-byte.
    [DllExport("XInputGetState", CallingConvention.StdCall)]
    public static int XInputGetState(uint dwUserIndex, out XInputState pState)
    {
        pState = default;

        if (dwUserIndex >= XInputSharedMemory.MaxControllers)
            return ErrorDeviceNotConnected;

        RefreshCacheIfStale();

        // Slot 0 is the only one we publish; if you wire up a second Betop,
        // map it to slot 1 here.
        if (dwUserIndex != 0)
        {
            // Forward to the real XInput so a real Xbox controller still works
            // if the user has one plugged in alongside the Betop.
            if (RealGetState != null)
                return RealGetState(dwUserIndex, out pState);
            return ErrorDeviceNotConnected;
        }

        pState.PacketNumber = (uint)Environment.TickCount;
        pState.Gamepad = _cachedPad;
        return Success;
    }

    [DllExport("XInputSetState", CallingConvention.StdCall)]
    public static int XInputSetState(uint dwUserIndex, ref XInputVibration pVibration)
    {
        if (dwUserIndex >= XInputSharedMemory.MaxControllers)
            return ErrorDeviceNotConnected;

        if (dwUserIndex != 0)
        {
            if (RealSetState != null)
                return RealSetState(dwUserIndex, ref pVibration);
            return ErrorDeviceNotConnected;
        }

        // Write to slot 0; the service picks it up on the next gen-bump.
        int slotOffset = XInputSharedMemory.SlotOffset((int)dwUserIndex);
        XInputSharedMemory.Slot slot = default;
        View.Read(slotOffset, out slot);
        slot.LargeMotor = (byte)Math.Clamp((int)pVibration.LeftMotorSpeed, 0, 255);
        slot.SmallMotor = (byte)Math.Clamp((int)pVibration.RightMotorSpeed, 0, 255);
        slot.VibrationPacket++;
        View.Write(slotOffset, ref slot);

        XInputSharedMemory.Header hdr = default;
        View.Read(0, out hdr);
        hdr.OutputGen++;
        View.Write(0, ref hdr);
        _cachedMotors = (slot.LargeMotor, slot.SmallMotor);
        _lastSeenOutputGen = hdr.OutputGen;

        return Success;
    }

    [DllExport("XInputGetCapabilities", CallingConvention.StdCall)]
    public static int XInputGetCapabilities(uint dwUserIndex, uint dwFlags, out XInputCapabilities pCaps)
    {
        pCaps = default;
        if (dwUserIndex == 0)
        {
            pCaps.Type = 1; // XINPUT_DEVTYPE_GAMEPAD
            pCaps.SubType = 1; // XINPUT_DEVSUBTYPE_GAMEPAD
            pCaps.Flags = 0;
            // Report every button / axis as supported.
            pCaps.Gamepad.Buttons = 0xFFFF;
            pCaps.Gamepad.LeftTrigger = 0xFF;
            pCaps.Gamepad.RightTrigger = 0xFF;
            pCaps.Gamepad.ThumbLX = short.MinValue;
            pCaps.Gamepad.ThumbLY = short.MaxValue;
            pCaps.Gamepad.ThumbRX = short.MinValue;
            pCaps.Gamepad.ThumbRY = short.MaxValue;
            pCaps.Vibration.LeftMotorSpeed = 0xFF;
            pCaps.Vibration.RightMotorSpeed = 0xFF;
            return Success;
        }
        return RealGetCaps != null
            ? RealGetCaps(dwUserIndex, dwFlags, out pCaps)
            : ErrorDeviceNotConnected;
    }

    [DllExport("XInputEnable", CallingConvention.StdCall)]
    public static void XInputEnable(bool enable)
    {
        RealEnable?.Invoke(enable);
    }

    [DllExport("XInputGetBatteryType", CallingConvention.StdCall)]
    public static uint XInputGetBatteryType(uint dwUserIndex, byte devType, out XInputBatteryInfo info)
    {
        info = default;
        if (dwUserIndex == 0)
        {
            // Wired controller: battery type = 0xFF (DISCONNECTED-equivalent),
            // level = 0xFF. We just want the call to succeed.
            info.BatteryType = 0;
            info.BatteryLevel = 0xFF;
            return Success;
        }
        return RealGetBattery != null
            ? RealGetBattery(dwUserIndex, devType, out info)
            : ErrorDeviceNotConnected;
    }

    [DllExport("XInputGetKeystroke", CallingConvention.StdCall)]
    public static uint XInputGetKeystroke(uint dwUserIndex, uint dwReserved, out XInputKeystroke pStroke)
    {
        pStroke = default;
        if (dwUserIndex == 0)
        {
            // We don't synthesize keystroke events; return empty success.
            return Success;
        }
        return RealGetKeystroke != null
            ? RealGetKeystroke(dwUserIndex, dwReserved, out pStroke)
            : ErrorDeviceNotConnected;
    }

    // ----- Internals -----
    private static void RefreshCacheIfStale()
    {
        XInputSharedMemory.Header hdr = default;
        View.Read(0, out hdr);
        if (hdr.Magic != XInputSharedMemory.MagicValue || hdr.Version != 1)
            return;
        if (hdr.InputGen == _lastSeenInputGen)
            return;

        int slotOffset = XInputSharedMemory.SlotOffset(0);
        XInputSharedMemory.Slot slot = default;
        View.Read(slotOffset, out slot);
        _cachedPad = slot.Pad;
        _lastSeenInputGen = hdr.InputGen;
    }

    private const int Success = 0;
    private const int ErrorDeviceNotConnected = 0x048F; // 1167
}
