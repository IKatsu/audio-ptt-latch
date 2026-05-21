using System.Runtime.InteropServices;

namespace AudioPttLatch;

/// <summary>
/// Emits synthetic keyboard input through the Windows SendInput API.
/// </summary>
public static class KeySender
{
    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    /// <summary>
    /// Sends a key-up event for the specified virtual key.
    /// </summary>
    public static void SendKeyUp(Keys key)
    {
        // SendInput emits the key-up that was intentionally swallowed by the hook.
        // Only key-up is sent because the physical key-down was allowed through.
        var input = new INPUT
        {
            type = INPUT_KEYBOARD,
            U = new InputUnion
            {
                ki = new KEYBDINPUT
                {
                    wVk = (ushort)key,
                    dwFlags = KEYEVENTF_KEYUP
                }
            }
        };

        NativeMethods.SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
    }

    /// <summary>
    /// Native INPUT structure used by SendInput.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion U;
    }

    /// <summary>
    /// INPUT is a tagged union in Win32; only the keyboard arm is needed here.
    /// </summary>
    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)]
        public KEYBDINPUT ki;
    }

    /// <summary>
    /// Native keyboard input payload for SendInput.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    /// <summary>
    /// P/Invoke declarations for synthetic input.
    /// </summary>
    private static class NativeMethods
    {
        [DllImport("user32.dll", SetLastError = true)]
        public static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
    }
}
