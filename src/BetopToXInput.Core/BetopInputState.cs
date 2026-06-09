namespace BetopToXInput.Core;

/// <summary>
/// Parsed state of one Betop C031 HID input report.
/// Layout was reverse-engineered from GAJOYPS.dll / GaJoyFF.dll (VID=0x0E8F, PID=0x0003).
/// Byte 0 is the report ID (varies by firmware). Remaining layout is best-effort and may
/// need calibration using a debug overlay; see <see cref="XInputStateMapper"/> for the
/// field offsets that we treat as canonical.
/// </summary>
public sealed class BetopInputState
{
    // Digital buttons
    public bool DPadUp;
    public bool DPadDown;
    public bool DPadLeft;
    public bool DPadRight;

    public bool ButtonA;        // Betop "A" (lower face button)
    public bool ButtonB;        // Betop "B" (right face button)
    public bool ButtonX;        // Betop "X" (left face button)
    public bool ButtonY;        // Betop "Y" (upper face button)

    public bool Start;
    public bool Back;
    public bool Guide;          // Big center logo button
    public bool LeftStick;      // L3
    public bool RightStick;     // R3

    public bool LeftShoulder;   // LB
    public bool RightShoulder;  // RB

    // Triggers are usually exposed as analog axes on DirectInput
    // (0..255 in HID report). We expose them as both bool and byte.
    public byte LeftTrigger;    // LT (0..255)
    public byte RightTrigger;   // RT (0..255)

    // Analog sticks: signed 16-bit, center ~0.
    public short LeftStickX;
    public short LeftStickY;
    public short RightStickX;
    public short RightStickY;

    /// <summary>True when at least one field has been populated.</summary>
    public bool HasData { get; private set; }

    public void Reset() => HasData = false;

    public void MarkUpdated() => HasData = true;
}
