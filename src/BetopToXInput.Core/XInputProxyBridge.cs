using System;
using System.IO.MemoryMappedFiles;
using System.Threading;
using System.Threading.Tasks;
using BetopToXInput.Core;

namespace BetopToXInput.Core;

/// <summary>
/// Bridge between the Betop HID hardware and the XInput proxy DLL. Lives in
/// the long-running <c>BetopToXInput.App</c> process. The DLL itself runs
/// inside the game; this service writes the latest gamepad state into a
/// shared memory file and reads vibration commands back.
/// </summary>
public sealed class XInputProxyBridge : IDisposable
{
    private readonly BetopHidReader _reader;
    private readonly BetopRumbleWriter _rumble;
    private readonly MemoryMappedFile _mmf;
    private readonly MemoryMappedViewAccessor _view;
    private readonly CancellationTokenSource _cts = new();

    private uint _lastWrittenInputGen;
    private uint _lastSeenOutputGen;
    private byte _lastLarge;
    private byte _lastSmall;

    public XInputProxyBridge()
    {
        _reader = new BetopHidReader();
        _rumble = new BetopRumbleWriter();

        int total = XInputSharedMemory.TotalSize;

        // The service creates the file first; the DLL only opens it. The DLL's
        // CreateOrOpen means order doesn't matter, but creating here gives a
        // known-valid layout.
        _mmf = MemoryMappedFile.CreateOrOpen(XInputSharedMemory.MapName, total);
        _view = _mmf.CreateViewAccessor();

        // Initialise header.
        var hdr = new XInputSharedMemory.Header
        {
            Magic = XInputSharedMemory.MagicValue,
            Version = 1,
            InputGen = 1,
            OutputGen = 1
        };
        _view.Write(0, ref hdr);
    }

    public void Start()
    {
        _reader.InputReceived += OnInput;
        _reader.Start();

        // Polling task: read vibration commands from the proxy side.
        Task.Run(PollVibrationLoop, _cts.Token);
    }

    private void OnInput(BetopInputState state)
    {
        var pad = XInputStateMapper.ToXInput(state);
        int slotOffset = XInputSharedMemory.SlotOffset(0);
        var slot = new XInputSharedMemory.Slot
        {
            Pad = pad,
            LargeMotor = _lastLarge,
            SmallMotor = _lastSmall,
            VibrationPacket = 1
        };
        _view.Write(slotOffset, ref slot);

        XInputSharedMemory.Header hdr = default;
        _view.Read(0, out hdr);
        hdr.InputGen++;
        _view.Write(0, ref hdr);
        _lastWrittenInputGen = hdr.InputGen;
    }

    private async Task PollVibrationLoop()
    {
        var token = _cts.Token;
        try
        {
            while (!token.IsCancellationRequested)
            {
                XInputSharedMemory.Header hdr = default;
                _view.Read(0, out hdr);
                if (hdr.Magic != XInputSharedMemory.MagicValue || hdr.Version != 1)
                {
                    await Task.Delay(50, token);
                    continue;
                }

                if (hdr.OutputGen != _lastSeenOutputGen)
                {
                    int slotOffset = XInputSharedMemory.SlotOffset(0);
                    XInputSharedMemory.Slot slot = default;
                    _view.Read(slotOffset, out slot);

                    // Avoid spamming the device: only push if the value changed.
                    if (slot.LargeMotor != _lastLarge || slot.SmallMotor != _lastSmall)
                    {
                        _rumble.SetRumble(slot.LargeMotor / 255.0, slot.SmallMotor / 255.0);
                        _lastLarge = slot.LargeMotor;
                        _lastSmall = slot.SmallMotor;
                    }
                    _lastSeenOutputGen = hdr.OutputGen;
                }

                await Task.Delay(10, token); // 100 Hz is enough
            }
        }
        catch (OperationCanceledException) { /* graceful */ }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Vibration poll loop error: {ex}");
        }
    }

    public void Stop()
    {
        _cts.Cancel();
        _reader.Stop();
        _rumble.Stop();
    }

    public void Dispose()
    {
        try
        {
            Stop();
            _reader.InputReceived -= OnInput;
            _reader.Dispose();
            _rumble.Dispose();
            _view.Dispose();
            _mmf.Dispose();
        }
        catch
        {
            // ignore
        }
        _cts.Dispose();
    }
}
