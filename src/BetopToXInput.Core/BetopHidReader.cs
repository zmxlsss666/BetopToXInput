using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace BetopToXInput.Core;

/// <summary>
/// Reads HID input reports from the Betop C031 using the Windows RawInput API
/// with <c>RIDEV_INPUTSINK</c>. The key advantage over the direct CreateFileW
/// approach: RawInput can receive reports even when another process (such as
/// the Betop vendor driver or a DirectInput consumer) has the HID device
/// opened exclusively.
///
/// Implementation note: this class uses <see cref="NativeWindow"/> with a
/// message-only parent (HWND_MESSAGE) and a hand-rolled GetMessage loop, NOT
/// a Form + Application.Run. The Form approach has known issues delivering
/// WM_INPUT reliably to hidden forms, especially when the form is also being
/// shown then immediately hidden. NativeWindow + manual pump is the
/// "blessed" way to receive RawInput data without a visible window.
/// </summary>
public sealed class BetopHidReader : IDisposable
{
    public const int BetopVendorId = 0x0E8F;
    public const int BetopProductId = 0x0003;

    private readonly HidPumpWindow? _window;
    private readonly Thread? _thread;
    private readonly ManualResetEventSlim _ready = new();
    private long _lastBetopDiagMs;
    private bool _disposed;

    public event Action<BetopInputState>? InputReceived;
    public event Action<byte[]>? RawInputReceived;
    public event Action<byte[], string>? AnyHIDInputReceived;

    /// <summary>
    /// When true, the reader will not drop non-Betop HID reports in
    /// <see cref="OnHidInput"/> — useful for <c>--dump-all</c> diagnostics.
    /// Default is false; service mode should never set this.
    /// </summary>
    public bool DumpAllHids { get; set; }

    public BetopHidReader()
    {
        try
        {
            _thread = new Thread(EntryPoint)
            {
                IsBackground = true,
                Name = "Betop RawInput pump"
            };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();

            if (!_ready.Wait(TimeSpan.FromSeconds(5)))
            {
                throw new TimeoutException(
                    "RawInput message window failed to start within 5 seconds.");
            }

            _window = _windowRef;
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    private HidPumpWindow? _windowRef;

    private void EntryPoint()
    {
        try
        {
            var w = new HidPumpWindow(this);
            _windowRef = w;
            _ready.Set();

            // Hand-rolled message loop. We need to register raw input AFTER
            // the HWND exists, so HidPumpWindow does that in HandleCreated.
            w.PumpUntilExit();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"RawInput pump crashed: {ex}");
        }
    }

    public void Start() { /* nothing to do - already pumping */ }
    public void Stop() { /* nothing to do - runs until Dispose */ }

    internal void OnHidInput(IntPtr hRawInput)
    {
        try
        {
            if (hRawInput == IntPtr.Zero)
            {
                Console.Error.WriteLine("OnHidInput: hRawInput is zero");
                return;
            }

            int headerSizeOf = Marshal.SizeOf<Rawinputheader>();
            int firstResult = (int)GetRawInputData(hRawInput, RID_INPUT, IntPtr.Zero, out uint size, (uint)headerSizeOf);
            if (firstResult != 0)
            {
                Console.Error.WriteLine($"OnHidInput: first GetRawInputData returned {firstResult}, expected 0");
                return;
            }
            if (size == 0 || size > 65536)
            {
                Console.Error.WriteLine($"OnHidInput: bad size {size}");
                return;
            }

            IntPtr buffer = Marshal.AllocHGlobal((int)size);
            try
            {
                uint sizeOut = size;
                int secondResult = (int)GetRawInputData(hRawInput, RID_INPUT, buffer, out sizeOut, (uint)headerSizeOf);
                if (secondResult != (int)size)
                {
                    Console.Error.WriteLine($"OnHidInput: second GetRawInputData returned {secondResult}, expected {size}");
                    return;
                }

                var raw = Marshal.PtrToStructure<Rawinput>(buffer);
                if (raw.header.dwType != RIM_TYPEHID)
                {
                    if ((DateTime.UtcNow.Ticks % 100) < 5)
                        Console.Error.WriteLine($"OnHidInput: dwType={raw.header.dwType} (not HID)");
                    return;
                }

                string? devicePath = GetDevicePath(raw.header.hDevice);
                bool isBetop = devicePath != null
                    && devicePath.Contains("VID_0E8F", StringComparison.OrdinalIgnoreCase)
                    && devicePath.Contains("PID_0003", StringComparison.OrdinalIgnoreCase);

                // Drop non-Betop HID reports early unless verbose logging is on.
                // (Without this guard, a Microsoft mouse at 1000 Hz would
                // flood the log every 1 ms and bury the actual Betop reports.)
                if (!isBetop && !DumpAllHids)
                {
                    return;
                }

                // raw.data.dwSizeHid is the size of ONE HID report.
                // raw.data.dwCount is the number of reports in this message.
                int reportSize = (int)raw.data.dwSizeHid;
                int reportCount = (int)raw.data.dwCount;
                if (reportSize <= 0 || reportCount <= 0)
                {
                    Console.Error.WriteLine($"OnHidInput: bad report dims {reportSize} x {reportCount}");
                    return;
                }

                // The raw reports begin right after the RAWHID struct header
                // (dwSizeHid + dwCount), which sits right after the RAWINPUTHEADER.
                // i.e. offset = sizeof(RAWINPUTHEADER) + 8 bytes of RAWHID prelude.
                int rawDataOffset = headerSizeOf + 8;
                int rawDataSize = reportSize * reportCount;
                if (rawDataOffset + rawDataSize > (int)size)
                {
                    Console.Error.WriteLine($"OnHidInput: offset+size {rawDataOffset}+{rawDataSize} > buffer {size}");
                    return;
                }

                // Read each report (in case dwCount > 1).
                for (int i = 0; i < reportCount; i++)
                {
                    byte[] bytes = new byte[reportSize];
                    Marshal.Copy(buffer + rawDataOffset + i * reportSize, bytes, 0, reportSize);

                    // Rate-limit diagnostic spam from the Betop (it polls at 250Hz).
                    // We still hand the bytes to subscribers — only the console is throttled.
                    if (isBetop)
                    {
                        long nowMs = Environment.TickCount64;
                        if (nowMs - _lastBetopDiagMs > 125)
                        {
                            _lastBetopDiagMs = nowMs;
                            var hex2 = BitConverter.ToString(bytes).Replace("-", " ");
                            Console.Error.WriteLine($"[betop {reportSize}B] {hex2}");
                        }
                    }
                    else
                    {
                        var hex2 = BitConverter.ToString(bytes).Replace("-", " ");
                        Console.Error.WriteLine($"[hid {reportSize}B other] {hex2}  path={devicePath ?? "<null>"}");
                    }

                    AnyHIDInputReceived?.Invoke(bytes, devicePath ?? "<unknown>");

                    if (isBetop)
                    {
                        RawInputReceived?.Invoke(bytes);
                        var state = new BetopInputState();
                        if (ParseReport(bytes, state))
                        {
                            InputReceived?.Invoke(state);
                        }
                    }
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"RawInput dispatch error: {ex}");
        }
    }

    private static string? GetDevicePath(IntPtr hDevice)
    {
        uint cch = 0;
        GetRawInputDeviceInfo(hDevice, RIDI_DEVICENAME, IntPtr.Zero, ref cch);
        if (cch == 0) return null;
        IntPtr p = Marshal.AllocHGlobal((int)(cch + 1) * 2);
        try
        {
            if (GetRawInputDeviceInfo(hDevice, RIDI_DEVICENAME, p, ref cch) <= 0) return null;
            return Marshal.PtrToStringUni(p);
        }
        finally
        {
            Marshal.FreeHGlobal(p);
        }
    }

    public static bool IsDevicePresent() => true;

    /// <summary>
    /// Convert raw HID report bytes into <see cref="BetopInputState"/>.
    ///
    /// Layout of the 9-byte Betop C031 report, confirmed via --calibrate:
    ///
    ///   b0  0x00   Report ID
    ///   b1  0x7F   Right stick Y (centered at 0x80, range 0..0xFF)
    ///   b2  0x7F   Right stick X
    ///   b3  0x7F   Left  stick X  (varies when stick pushed: e.g. 0x6F on left push)
    ///   b4  0x80   Left  stick Y  (e.g. 0x00 on full up push)
    ///   b5  0xFF   Always 0xFF in idle — looks like a header/sync byte, not buttons
    ///   b6  0x0F   Low  nibble = 4-bit hat switch (0x0F = centered, 0x00 = N, 0x04 = S, ...)
    ///                High nibble = face buttons:  0x10=Y, 0x20=B, 0x40=A, 0x80=X (assumed)
    ///   b7  0x00   Bit-pack: 0x01=LB, 0x02=RB, 0x04=LT(digital), 0x10=Back, 0x20=Start(assumed)
    ///   b8  0x00   Always 0x00 in idle (unverified; may hold other bits)
    /// </summary>
    public static bool ParseReport(byte[] data, BetopInputState state)
    {
        if (data == null || data.Length < 7) return false;

        byte b1 = data[1];
        byte b2 = data[2];
        byte b3 = data[3];
        byte b4 = data[4];
        byte b6 = data[6];
        byte b7 = data.Length > 7 ? data[7] : (byte)0;
        byte b8 = data.Length > 8 ? data[8] : (byte)0;

        // D-pad: low nibble of b6 is a standard 4-bit hat switch.
        //   0x0F = "nothing pressed" (the center position).
        //   0    = N, 1=NE, 2=E, 3=SE, 4=S, 5=SW, 6=W, 7=NW
        byte hat = (byte)(b6 & 0x0F);
        if (hat != 0x0F)
        {
            state.DPadUp    = hat == 0 || hat == 1 || hat == 7;
            state.DPadRight = hat == 1 || hat == 2 || hat == 3;
            state.DPadDown  = hat == 3 || hat == 4 || hat == 5;
            state.DPadLeft  = hat == 5 || hat == 6 || hat == 7;
        }
        else
        {
            state.DPadUp = state.DPadDown = state.DPadLeft = state.DPadRight = false;
        }

        // Face buttons: high nibble of b6.
        state.ButtonY = (b6 & 0x10) != 0;
        state.ButtonB = (b6 & 0x20) != 0;
        state.ButtonA = (b6 & 0x40) != 0;
        state.ButtonX = (b6 & 0x80) != 0; // assumed; not yet seen in calibration

        // Shoulders, back, start, guide.  Bit positions from calibration.
        state.LeftShoulder  = (b7 & 0x01) != 0;
        state.RightShoulder = (b7 & 0x02) != 0;
        state.Back          = (b7 & 0x10) != 0;
        state.Start         = (b7 & 0x20) != 0; // assumed
        state.Guide         = (b7 & 0x40) != 0; // guess
        state.LeftStick     = (b8 & 0x40) != 0; // L3 — guess (b8 layout unverified)
        state.RightStick    = (b8 & 0x80) != 0; // R3 — guess

        // Triggers: the calibration did not show analog trigger values, only
        // a digital LT bit.  Use 0/255 as a placeholder for now.  RT is not
        // visible at all in the captured data.
        state.LeftTrigger  = (b7 & 0x04) != 0 ? (byte)255 : (byte)0;
        state.RightTrigger = (b7 & 0x08) != 0 ? (byte)255 : (byte)0; // unverified

        // Sticks: raw 8-bit unsigned centered around 0x80.  We keep them as
        // shorts in the same 0..255 range here; the XInputStateMapper does
        // the 0x80 -> 0 offset and the 8x scale to XInput 16-bit.
        //
        // Betop C031 (verified): b1 = right Y, b2 = right X.  Left stick
        // is the conventional b3=X, b4=Y.
        state.RightStickY = b1;
        state.RightStickX = b2;
        state.LeftStickX  = b3;
        state.LeftStickY  = b4;

        state.MarkUpdated();
        return true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            _window?.RequestExit();
            if (_thread != null && _thread.IsAlive) _thread.Join(2000);
        }
        catch { /* ignore */ }
        try { _window?.DestroyHandle(); } catch { }
        _ready.Dispose();
    }

    // ----- NativeWindow-based host for the message loop -----
    private sealed class HidPumpWindow : NativeWindow
    {
        private readonly BetopHidReader _owner;
        private bool _registered;
        private bool _exitRequested;
        // HWND_MESSAGE = ((HWND)-3) — parent for windows that never appear on screen.
        private static readonly IntPtr HWND_MESSAGE = new(-3);

        public HidPumpWindow(BetopHidReader owner)
        {
            _owner = owner;

            var cp = new CreateParams
            {
                Caption = "BetopToXInput.RawInput",
                X = 0, Y = 0, Width = 0, Height = 0,
                Parent = HWND_MESSAGE
            };
            CreateHandle(cp);
        }

        private void TryRegister()
        {
            if (_registered) return;
            if (Handle == IntPtr.Zero) return;

            // Register BOTH Gamepad (1/5) AND Joystick (1/4). Some Betop
            // firmwares (and the Windows HID class driver) register the
            // device as a Joystick instead of a Gamepad.
            var devices = new[]
            {
                new Rawinputdevice
                {
                    usUsagePage = 0x01, usUsage = 0x05,
                    dwFlags = RIDEV_INPUTSINK, hwndTarget = Handle
                },
                new Rawinputdevice
                {
                    usUsagePage = 0x01, usUsage = 0x04,
                    dwFlags = RIDEV_INPUTSINK, hwndTarget = Handle
                }
            };

            bool ok = RegisterRawInputDevices(devices, (uint)devices.Length,
                (uint)Marshal.SizeOf<Rawinputdevice>());
            if (!ok)
            {
                int err = Marshal.GetLastWin32Error();
                Console.Error.WriteLine($"RegisterRawInputDevices failed: Win32 error {err}");
            }
            else
            {
                _registered = true;
                Console.Error.WriteLine("RawInput registered (Gamepad + Joystick). Waiting for input.");
            }
        }

        public void RequestExit() => _exitRequested = true;

        public void PumpUntilExit()
        {
            var msg = new MSG();
            while (!_exitRequested)
            {
                int r = GetMessage(ref msg, IntPtr.Zero, 0, 0);
                if (r <= 0) break; // 0 = WM_QUIT, -1 = error
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_INPUT)
            {
                _owner.OnHidInput(m.LParam);
            }
            else if (m.Msg == WM_CREATE)
            {
                // Window handle is now valid - register raw input devices.
                TryRegister();
            }
            else if (m.Msg == WM_DESTROY)
            {
                _exitRequested = true;
            }
            base.WndProc(ref m);
        }
    }

    // ----- P/Invoke -----
    private const int WM_INPUT = 0x00FF;
    private const int WM_DESTROY = 0x0002;
    private const int WM_CREATE = 0x0001;
    private const int RID_INPUT = 0x10000003;
    private const uint RIM_TYPEHID = 2;
    private const uint RIDI_DEVICENAME = 0x20000007;
    private const uint RIDEV_INPUTSINK = 0x00000100;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterRawInputDevices(
        Rawinputdevice[] pRawInputDevices, uint uiNumDevices, uint cbSize);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRawInputData(
        IntPtr hRawInput, uint uiCommand, IntPtr pData, out uint pcbSize, uint cbSizeHeader);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetRawInputDeviceInfo(
        IntPtr hDevice, uint uiCommand, IntPtr pData, ref uint pcbSize);

    [DllImport("user32.dll")]
    private static extern int GetMessage(ref MSG msg, IntPtr hWnd, uint min, uint max);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG msg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref MSG msg);

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int pt_x;
        public int pt_y;
        // 32-bit lParam for POINT (x,y) on x86; on x64 these two ints
        // (8 bytes) are unused and the point is at pt_x/pt_y as longs.
        public int lPrivate;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rawinputdevice
    {
        public ushort usUsagePage;
        public ushort usUsage;
        public uint dwFlags;
        public IntPtr hwndTarget;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rawinputheader
    {
        public uint dwType;
        public uint dwSize;
        public IntPtr hDevice;
        public IntPtr wParam;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rawhid
    {
        public uint dwSizeHid;
        public uint dwCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rawinput
    {
        public Rawinputheader header;
        public Rawhid data;
    }
}
