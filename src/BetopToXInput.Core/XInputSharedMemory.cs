using System;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;

namespace BetopToXInput.Core;

/// <summary>
/// Process boundary shared memory contract between:
///   - <c>BetopToXInput.Proxy.dll</c> (loaded by the game; runs inside the game process)
///   - <c>BetopToXInput.App</c> (the long-running service that owns the Betop HID)
///
/// Layout is two sections, separated by a small header. Reads are lock-free
/// because both sides follow a "writer increments generation last" discipline.
/// </summary>
public static class XInputSharedMemory
{
    public const string MapName = @"Local\BetopToXInput.SharedState.v1";
    public const string MutexName = @"Local\BetopToXInput.SharedState.v1.Mutex";

    public const int MaxControllers = 4;

    public const uint MagicValue = 0x49585442; // 'B','T','X','I' little-endian

    [StructLayout(LayoutKind.Sequential)]
    public struct Header
    {
        public uint Magic;
        public uint Version;
        public uint InputGen;     // bumped by the service when gamepad state changes
        public uint OutputGen;    // bumped by the proxy when vibration changes
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Slot
    {
        public XInputGamepad Pad;        // 12 bytes
        public ushort VibrationPacket;   // monotonically increasing
        public byte LargeMotor;          // 0..255
        public byte SmallMotor;          // 0..255
        public uint _padding;            // align to 8 bytes
    }

    public static int TotalSize => Marshal.SizeOf<Header>() + MaxControllers * Marshal.SizeOf<Slot>();
    public static int SlotOffset(int index) => Marshal.SizeOf<Header>() + index * Marshal.SizeOf<Slot>();
}
