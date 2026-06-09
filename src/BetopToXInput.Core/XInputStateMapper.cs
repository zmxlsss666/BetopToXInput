using System;

namespace BetopToXInput.Core;

/// <summary>
/// Maps <see cref="BetopInputState"/> to an Xbox 360 controller state (the same layout
/// that XInput / ViGEm / XInput proxy use). Pure function; no I/O.
/// </summary>
public static class XInputStateMapper
{
    /// <summary>
    /// Build an Xbox 360 state. <paramref name="deadzone"/> is the radial dead-zone
    /// applied to analog sticks (0..1 of the half-range).
    /// </summary>
    public static XInputGamepad ToXInput(in BetopInputState s, double deadzone = 0.08)
    {
        var g = new XInputGamepad();

        g.Buttons |= (ushort)(s.DPadUp    ? (ushort)XInputButtons.DPadUp    : (ushort)0);
        g.Buttons |= (ushort)(s.DPadDown  ? (ushort)XInputButtons.DPadDown  : (ushort)0);
        g.Buttons |= (ushort)(s.DPadLeft  ? (ushort)XInputButtons.DPadLeft  : (ushort)0);
        g.Buttons |= (ushort)(s.DPadRight ? (ushort)XInputButtons.DPadRight : (ushort)0);

        g.Buttons |= (ushort)(s.ButtonA ? (ushort)XInputButtons.A : (ushort)0);
        g.Buttons |= (ushort)(s.ButtonB ? (ushort)XInputButtons.B : (ushort)0);
        g.Buttons |= (ushort)(s.ButtonX ? (ushort)XInputButtons.X : (ushort)0);
        g.Buttons |= (ushort)(s.ButtonY ? (ushort)XInputButtons.Y : (ushort)0);

        g.Buttons |= (ushort)(s.Start ? (ushort)XInputButtons.Start : (ushort)0);
        g.Buttons |= (ushort)(s.Back  ? (ushort)XInputButtons.Back  : (ushort)0);
        g.Buttons |= (ushort)(s.Guide ? (ushort)XInputButtons.Guide : (ushort)0);
        g.Buttons |= (ushort)(s.LeftStick  ? (ushort)XInputButtons.LeftThumb  : (ushort)0);
        g.Buttons |= (ushort)(s.RightStick ? (ushort)XInputButtons.RightThumb : (ushort)0);
        g.Buttons |= (ushort)(s.LeftShoulder  ? (ushort)XInputButtons.LeftShoulder  : (ushort)0);
        g.Buttons |= (ushort)(s.RightShoulder ? (ushort)XInputButtons.RightShoulder : (ushort)0);

        // Triggers go through 0..255, exactly as XInput expects.
        g.LeftTrigger  = s.LeftTrigger;
        g.RightTrigger = s.RightTrigger;

        // Sticks: apply radial dead-zone, then map -32768..32767 to INT16_MIN..INT16_MAX.
        g.ThumbLX = ApplyStickAxis(s.LeftStickX,  deadzone);
        g.ThumbLY = ApplyStickAxis(s.LeftStickY,  deadzone);
        g.ThumbRX = ApplyStickAxis(s.RightStickX, deadzone);
        g.ThumbRY = ApplyStickAxis(s.RightStickY, deadzone);

        return g;
    }

    private static short ApplyStickAxis(short raw, double deadzone)
    {
        // The Betop sticks are usually centered near 0; treat the value as a
        // fraction of short range with a tiny dead-zone.
        const double max = short.MaxValue; // 32767
        double n = raw / max;
        double mag = Math.Abs(n);
        if (mag < deadzone) return 0;
        // Re-scale the magnitude so the value just outside the dead-zone is small.
        double scaled = (mag - deadzone) / (1.0 - deadzone);
        scaled = Math.Min(scaled, 1.0) * Math.Sign(n);
        return (short)(scaled * max);
    }
}

[Flags]
public enum XInputButtons : ushort
{
    DPadUp        = 0x0001,
    DPadDown      = 0x0002,
    DPadLeft      = 0x0004,
    DPadRight     = 0x0008,
    Start         = 0x0010,
    Back          = 0x0020,
    LeftThumb     = 0x0040,
    RightThumb    = 0x0080,
    LeftShoulder  = 0x0100,
    RightShoulder = 0x0200,
    Guide         = 0x0400,
    A             = 0x1000,
    B             = 0x2000,
    X             = 0x4000,
    Y             = 0x8000
}

/// <summary>
/// Xbox 360 gamepad state, byte layout compatible with XINPUT_GAMEPAD.
/// </summary>
[System.Runtime.InteropServices.StructLayout(
    System.Runtime.InteropServices.LayoutKind.Sequential)]
public struct XInputGamepad
{
    public ushort Buttons;
    public byte LeftTrigger;
    public byte RightTrigger;
    public short ThumbLX;
    public short ThumbLY;
    public short ThumbRX;
    public short ThumbRY;
}
