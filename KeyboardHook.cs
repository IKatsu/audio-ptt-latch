using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AudioPttLatch;

/// <summary>
/// Wraps a global low-level keyboard hook and exposes physical key down/up events.
/// The hook can optionally suppress a key event by marking the event args as handled.
/// </summary>
public sealed class KeyboardHook : IDisposable
{
    // Win32 constants used by SetWindowsHookEx and the low-level keyboard callback.
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP = 0x0105;
    private const int LLKHF_INJECTED = 0x00000010;

    private readonly LowLevelKeyboardProc _callback;
    private IntPtr _hookHandle;

    /// <summary>
    /// Prepares the unmanaged callback delegate used by Windows.
    /// </summary>
    public KeyboardHook()
    {
        // Keep the delegate rooted for the full hook lifetime. If it is collected,
        // Windows would call back into a dead function pointer.
        _callback = HookCallback;
    }

    /// <summary>
    /// Raised for physical key-down events observed by the hook.
    /// </summary>
    public event EventHandler<KeyboardHookEventArgs>? KeyDown;

    /// <summary>
    /// Raised for physical key-up events observed by the hook.
    /// </summary>
    public event EventHandler<KeyboardHookEventArgs>? KeyUp;

    /// <summary>
    /// Installs the low-level keyboard hook for the current desktop session.
    /// </summary>
    public void Start()
    {
        if (_hookHandle != IntPtr.Zero)
        {
            return;
        }

        using var process = Process.GetCurrentProcess();
        using var module = process.MainModule;
        var moduleHandle = module == null ? IntPtr.Zero : NativeMethods.GetModuleHandle(module.ModuleName);
        _hookHandle = NativeMethods.SetWindowsHookEx(WH_KEYBOARD_LL, _callback, moduleHandle, 0);
        if (_hookHandle == IntPtr.Zero)
        {
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    /// <summary>
    /// Removes the keyboard hook if it is installed.
    /// </summary>
    public void Stop()
    {
        if (_hookHandle == IntPtr.Zero)
        {
            return;
        }

        NativeMethods.UnhookWindowsHookEx(_hookHandle);
        _hookHandle = IntPtr.Zero;
    }

    /// <summary>
    /// Ensures the native hook is removed when this object is disposed.
    /// </summary>
    public void Dispose() => Stop();

    /// <summary>
    /// Win32 callback invoked for every low-level keyboard event.
    /// Returns a non-zero value to suppress the event from reaching other apps.
    /// </summary>
    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var data = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            var message = wParam.ToInt32();
            var injected = (data.flags & LLKHF_INJECTED) != 0;

            // Ignore synthetic input from SendInput so the delayed release does not
            // get treated like a fresh physical key event.
            if (!injected && (message == WM_KEYDOWN || message == WM_SYSKEYDOWN))
            {
                var args = new KeyboardHookEventArgs((Keys)data.vkCode);
                KeyDown?.Invoke(this, args);
                if (args.Handled)
                {
                    return new IntPtr(1);
                }
            }
            else if (!injected && (message == WM_KEYUP || message == WM_SYSKEYUP))
            {
                var args = new KeyboardHookEventArgs((Keys)data.vkCode);
                KeyUp?.Invoke(this, args);
                if (args.Handled)
                {
                    return new IntPtr(1);
                }
            }
        }

        return NativeMethods.CallNextHookEx(_hookHandle, nCode, wParam, lParam);
    }

    /// <summary>
    /// Delegate signature required by SetWindowsHookEx.
    /// </summary>
    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    /// <summary>
    /// Native event payload supplied by WH_KEYBOARD_LL.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public int flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    /// <summary>
    /// P/Invoke declarations for the user32/kernel32 calls used by the hook.
    /// </summary>
    private static class NativeMethods
    {
        // Low-level keyboard hooks are process-wide callbacks provided by user32.
        // They let us decide whether a key event should continue to other apps.
        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        public static extern IntPtr GetModuleHandle(string? lpModuleName);
    }
}

/// <summary>
/// Event data passed from the low-level hook to the latch controller.
/// </summary>
public sealed class KeyboardHookEventArgs : EventArgs
{
    /// <summary>
    /// Creates key event data for one virtual key.
    /// </summary>
    public KeyboardHookEventArgs(Keys key)
    {
        Key = key;
    }

    /// <summary>
    /// Virtual key observed by the hook.
    /// </summary>
    public Keys Key { get; }

    /// <summary>
    /// Set true to prevent the event from being delivered to other applications.
    /// </summary>
    public bool Handled { get; set; }
}
