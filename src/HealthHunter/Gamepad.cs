using System.Runtime.InteropServices;

namespace HealthHunter;

/// <summary>
/// Reads Xbox-style controllers through XInput so hotkeys can be bound to button combos.
/// Read-only polling of the controller; nothing touches the game.
/// </summary>
public static class Gamepad
{
    [StructLayout(LayoutKind.Sequential)]
    private struct XINPUT_GAMEPAD
    {
        public ushort wButtons;
        public byte bLeftTrigger, bRightTrigger;
        public short sThumbLX, sThumbLY, sThumbRX, sThumbRY;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XINPUT_STATE
    {
        public uint dwPacketNumber;
        public XINPUT_GAMEPAD Gamepad;
    }

    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
    private static extern uint XInputGetState14(uint index, out XINPUT_STATE state);

    [DllImport("xinput9_1_0.dll", EntryPoint = "XInputGetState")]
    private static extern uint XInputGetState910(uint index, out XINPUT_STATE state);

    private static bool _use910, _unavailable;

    // Triggers are analog; report them as two extra "buttons" in bits XInput leaves unused.
    public const ushort LeftTrigger = 0x0400, RightTrigger = 0x0800;

    private static readonly (ushort Bit, string Name)[] Names =
    {
        (0x0001, "Up"), (0x0002, "Down"), (0x0004, "Left"), (0x0008, "Right"),
        (0x0010, "Start"), (0x0020, "Back"), (0x0040, "LS"), (0x0080, "RS"),
        (0x0100, "LB"), (0x0200, "RB"), (LeftTrigger, "LT"), (RightTrigger, "RT"),
        (0x1000, "A"), (0x2000, "B"), (0x4000, "X"), (0x8000, "Y")
    };

    /// <summary>Buttons held on any of the 4 controllers, OR-ed together. 0 when none / XInput missing.</summary>
    public static ushort Buttons()
    {
        if (_unavailable) return 0;
        ushort all = 0;
        for (uint i = 0; i < 4; i++)
        {
            XINPUT_STATE st;
            uint rc;
            try { rc = _use910 ? XInputGetState910(i, out st) : XInputGetState14(i, out st); }
            catch (DllNotFoundException)
            {
                if (_use910) { _unavailable = true; return 0; }
                _use910 = true; i--; continue;
            }
            catch (EntryPointNotFoundException) { _unavailable = true; return 0; }
            if (rc != 0) continue; // not connected
            ushort b = (ushort)(st.Gamepad.wButtons & ~(LeftTrigger | RightTrigger));
            if (st.Gamepad.bLeftTrigger > 128) b |= LeftTrigger;
            if (st.Gamepad.bRightTrigger > 128) b |= RightTrigger;
            all |= b;
        }
        return all;
    }

    /// <summary>How far the left stick is pushed on any controller, 0..1 (v6: the assistant's "is the player walking").</summary>
    public static double LeftStick()
    {
        if (_unavailable) return 0;
        double best = 0;
        for (uint i = 0; i < 4; i++)
        {
            XINPUT_STATE st;
            uint rc;
            try { rc = _use910 ? XInputGetState910(i, out st) : XInputGetState14(i, out st); }
            catch (DllNotFoundException) { if (_use910) { _unavailable = true; return 0; } _use910 = true; i--; continue; }
            catch (EntryPointNotFoundException) { _unavailable = true; return 0; }
            if (rc != 0) continue;
            double x = st.Gamepad.sThumbLX / 32767.0, y = st.Gamepad.sThumbLY / 32767.0;
            best = Math.Max(best, Math.Sqrt(x * x + y * y));
        }
        return Math.Min(1, best);
    }

    public static string Describe(ushort mask) =>
        mask == 0 ? "(none)" : string.Join("+", Names.Where(n => (mask & n.Bit) != 0).Select(n => n.Name));
}
