using System;
using System.Threading;
using BetopToXInput.Core;
using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Targets;
using Nefarius.ViGEm.Client.Targets.Xbox360;

namespace BetopToXInput.ViGEm;

/// <summary>
/// Bridges <see cref="BetopInputState"/> to a virtual Xbox 360 controller exposed
/// by the ViGEmBus kernel driver.  The host process only needs the
/// <c>ViGEmBus</c> driver installed (e.g. from
/// https://github.com/ViGEm/ViGEmBus/releases) — the user-mode
/// <c>Nefarius.ViGEm.Client</c> package is consumed directly via NuGet.
///
/// API summary (probed from Nefarius.ViGEm.Client 1.21.223):
///   - <c>ViGEmClient.CreateXbox360Controller()</c> returns the concrete
///     <c>Xbox360Controller</c> implementing <c>IXbox360Controller</c>.
///   - Buttons, axes, and triggers are exposed as **settable properties** on
///     the controller (LeftTrigger / RightTrigger / LeftThumbX / etc.).
///   - Call <c>SubmitReport()</c> to push the current state to the bus.
///   - Vibration feedback arrives via the <c>FeedbackReceived</c> event with
///     <c>Xbox360FeedbackReceivedEventArgs.LargeMotor</c> /
///     <c>.SmallMotor</c> (both <c>ushort</c> 0..65535).
/// </summary>
public sealed class ViGEmBridge : IDisposable
{
    private readonly ViGEmClient _client;
    private readonly IXbox360Controller _pad;
    private bool _disposed;

    public ViGEmBridge()
    {
        _client = new ViGEmClient();
        _pad = _client.CreateXbox360Controller();
        _pad.AutoSubmitReport = false;
        _pad.FeedbackReceived += OnFeedback;
    }

    public bool IsConnected => !_disposed;

    public void PlugIn()
    {
        if (_disposed) return;
        _pad.Connect();
    }

    public void Unplug()
    {
        if (_disposed) return;
        _pad.Disconnect();
    }

    public void Submit(BetopInputState state)
    {
        if (_disposed) return;

        // Buttons - use the SetButtonState / SetButtonsFull API
        SetButton(Xbox360Button.Up,            state.DPadUp);
        SetButton(Xbox360Button.Down,          state.DPadDown);
        SetButton(Xbox360Button.Left,          state.DPadLeft);
        SetButton(Xbox360Button.Right,         state.DPadRight);
        SetButton(Xbox360Button.Start,         state.Start);
        SetButton(Xbox360Button.Back,          state.Back);
        SetButton(Xbox360Button.Guide,         state.Guide);
        SetButton(Xbox360Button.LeftShoulder,  state.LeftShoulder);
        SetButton(Xbox360Button.RightShoulder, state.RightShoulder);
        SetButton(Xbox360Button.LeftThumb,     state.LeftStick);
        SetButton(Xbox360Button.RightThumb,    state.RightStick);
        SetButton(Xbox360Button.A, state.ButtonA);
        SetButton(Xbox360Button.B, state.ButtonB);
        SetButton(Xbox360Button.X, state.ButtonX);
        SetButton(Xbox360Button.Y, state.ButtonY);

        // Axes - 16-bit signed centered at 0
        // XInput Y is positive-when-pushed-DOWN (matches screen coords).
        // Betop's stick Y is 0x80 at center, 0x00 pushed UP, 0xFF pushed DOWN,
        // so we just feed ScaleAxis straight in (no negation).
        _pad.SetAxisValue(Xbox360Axis.LeftThumbX,  ScaleAxis(state.LeftStickX));
        _pad.SetAxisValue(Xbox360Axis.LeftThumbY,  ScaleAxis(state.LeftStickY));
        _pad.SetAxisValue(Xbox360Axis.RightThumbX, ScaleAxis(state.RightStickX));
        _pad.SetAxisValue(Xbox360Axis.RightThumbY, ScaleAxis(state.RightStickY));

        // Triggers - 0..255
        _pad.SetSliderValue(Xbox360Slider.LeftTrigger,  state.LeftTrigger);
        _pad.SetSliderValue(Xbox360Slider.RightTrigger, state.RightTrigger);

        _pad.SubmitReport();
    }

    private void SetButton(Xbox360Button button, bool pressed)
    {
        _pad.SetButtonState(button, pressed);
    }

    public event Action<ushort, ushort>? RumbleReceived;

    private void OnFeedback(object? sender, Xbox360FeedbackReceivedEventArgs e)
    {
        RumbleReceived?.Invoke(e.LargeMotor, e.SmallMotor);
    }

    private static short ScaleAxis(short raw)
    {
        // Betop sticks: 8-bit unsigned centered at 0x80 (range 0..0xFF).
        // XInput sticks: 16-bit signed, range -32768..32767.
        if (raw < 0) raw = 0;
        if (raw > 0xFF) raw = 0xFF;
        int v = ((int)raw - 0x80) << 8;
        if (v < short.MinValue) return short.MinValue;
        if (v > short.MaxValue) return short.MaxValue;
        return (short)v;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            _pad.FeedbackReceived -= OnFeedback;
            _pad.Disconnect();
        }
        catch
        {
            // ignore disconnect failures during shutdown
        }
        _client.Dispose();
    }
}
