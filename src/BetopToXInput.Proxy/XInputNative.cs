using System;
using System.Runtime.InteropServices;
using BetopToXInput.Core;

namespace BetopToXInput.Proxy;

/// <summary>
/// Native-layout mirrors of the XInput 1.3 / 1.4 ABI. They must match
/// XINPUT_GAMEPAD / XINPUT_STATE / XINPUT_VIBRATION exactly, otherwise
/// the game will see garbage.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct XInputState
{
    public uint PacketNumber;
    public XInputGamepad Gamepad;
}

[StructLayout(LayoutKind.Sequential)]
public struct XInputVibration
{
    public ushort LeftMotorSpeed;
    public ushort RightMotorSpeed;
}

[StructLayout(LayoutKind.Sequential)]
public struct XInputCapabilities
{
    public byte Type;
    public byte SubType;
    public ushort Flags;
    public XInputGamepad Gamepad;
    public XInputVibration Vibration;
}

[StructLayout(LayoutKind.Sequential)]
public struct XInputBatteryInfo
{
    public byte BatteryType;
    public byte BatteryLevel;
}

[StructLayout(LayoutKind.Sequential)]
public struct XInputKeystroke
{
    public ushort VirtualKey;
    public ushort Unicode;
    public ushort Flags;
    public byte UserIndex;
    public byte HidCode;
}
