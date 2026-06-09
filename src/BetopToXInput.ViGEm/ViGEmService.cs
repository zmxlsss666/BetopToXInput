using System;
using System.Threading;
using BetopToXInput.Core;

namespace BetopToXInput.ViGEm;

/// <summary>
/// Wires the <see cref="BetopHidReader"/>'s input to a <see cref="ViGEmBridge"/>
/// (so the virtual Xbox 360 controller mirrors the physical gamepad) and
/// pumps feedback from the virtual controller back to the physical
/// Betop rumble motors.
/// </summary>
public sealed class ViGEmService : IDisposable
{
    private readonly BetopHidReader _reader;
    private readonly ViGEmBridge _bridge;
    private readonly BetopRumbleWriter _rumble;
    private readonly CancellationTokenSource _cts = new();
    private bool _disposed;

    public ViGEmService(BetopHidReader reader, ViGEmBridge bridge, BetopRumbleWriter rumble)
    {
        _reader = reader;
        _bridge = bridge;
        _rumble = rumble;

        _reader.InputReceived += OnInput;
        _bridge.RumbleReceived += OnRumble;
    }

    public void Start()
    {
        _reader.Start();
    }

    private void OnInput(BetopInputState state)
    {
        if (_disposed) return;
        try { _bridge.Submit(state); } catch { /* best effort */ }
    }

    private void OnRumble(ushort largeMotor, ushort smallMotor)
    {
        if (_disposed) return;
        // XInput spec says motors are 0..65535 (WORD).  Real games honour
        // this.  However some web testers (e.g. gamepadtester.cn) and
        // a few games send values in the 0..255 range instead.  Detect
        // the scale by looking at the magnitude: if either motor exceeds
        // 255 we assume the 0..65535 scale and take the high byte;
        // otherwise we treat the value as 0..255 directly.
        byte big = ScaleMotor(largeMotor);
        byte small = ScaleMotor(smallMotor);
        Console.Out.WriteLine($"[vrumble] raw large={largeMotor} (0x{largeMotor:X4}) small={smallMotor} (0x{smallMotor:X4}) -> bytes {big}/{small}");
        try { _rumble.SetRumble(big / 255.0, small / 255.0); } catch (Exception ex) { Console.Error.WriteLine($"[vrumble] SetRumble threw: {ex.Message}"); }
    }

    private static byte ScaleMotor(ushort v)
    {
        if (v > 255) return (byte)(v >> 8);   // 0..65535 -> 0..255
        if (v > byte.MaxValue) v = byte.MaxValue;
        return (byte)v;                        // 0..255 -> 0..255
    }

    public void Stop()
    {
        _cts.Cancel();
        _reader.Stop();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _reader.InputReceived -= OnInput; } catch { /* */ }
        try { _bridge.RumbleReceived -= OnRumble; } catch { /* */ }
        try { Stop(); } catch { /* */ }
        _cts.Dispose();
    }
}
