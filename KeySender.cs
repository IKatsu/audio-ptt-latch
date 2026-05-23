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
    /// Sends a key-down event for the specified virtual key.
    /// </summary>
    public static void SendKeyDown(Keys key)
    {
        SendKey(key, keyUp: false);
    }

    /// <summary>
    /// Sends a key-up event for the specified virtual key.
    /// </summary>
    public static void SendKeyUp(Keys key)
    {
        SendKey(key, keyUp: true);
    }

    /// <summary>
    /// Sends a normal press and release, useful for checking whether target apps receive synthetic input.
    /// </summary>
    public static void SendKeyPress(Keys key)
    {
        SendKeyDown(key);
        SendKeyUp(key);
    }

    /// <summary>
    /// Sends one keyboard transition using virtual-key input. This mirrors a normal
    /// Windows key event and is easy to verify in apps like Notepad.
    /// </summary>
    private static void SendKey(Keys key, bool keyUp)
    {
        var flags = keyUp ? KEYEVENTF_KEYUP : 0;

        // SendInput emits the key-up/down events that maintain the artificial latch.
        // The low-level hook ignores injected input so these events do not feed back.
        var input = new INPUT
        {
            type = INPUT_KEYBOARD,
            U = new InputUnion
            {
                ki = new KEYBDINPUT
                {
                    wVk = (ushort)key,
                    dwFlags = flags
                }
            }
        };

        var sent = NativeMethods.SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
        if (sent != 1)
        {
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }
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
        public MOUSEINPUT mi;

        [FieldOffset(0)]
        public KEYBDINPUT ki;

        [FieldOffset(0)]
        public HARDWAREINPUT hi;
    }

    /// <summary>
    /// Native mouse input payload. It is included so the INPUT union has the
    /// same size as the Win32 definition on x64, even though this app never sends mouse input.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
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
    /// Native hardware input payload. It completes the Win32 INPUT union layout.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL;
        public ushort wParamH;
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
