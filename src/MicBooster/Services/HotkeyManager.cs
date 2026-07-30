using System.Runtime.InteropServices;
using System.Windows.Interop;
using Keys = System.Windows.Forms.Keys;

namespace MicBooster.Services;

/// <summary>
/// Registers process-wide hotkeys through the Win32 hotkey table, so mute keeps working while
/// a game or another application has keyboard focus.
/// </summary>
/// <remarks>
/// Call every member from the UI thread. Windows delivers <c>WM_HOTKEY</c> to the message loop
/// of the window passed to <see cref="Attach"/>, and the hook that receives it runs on that
/// thread, which is where callbacks are invoked. Bindings made before <see cref="Attach"/> are
/// held and applied as soon as a window handle exists.
/// </remarks>
public sealed class HotkeyManager : IDisposable
{
    private const int WmHotkey = 0x0312;

    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModWin = 0x0008;

    /// <summary>Suppresses auto-repeat, so holding the key fires the action once.</summary>
    private const uint ModNoRepeat = 0x4000;

    private const int ErrorHotkeyAlreadyRegistered = 1409;

    /// <summary>
    /// Win32 requires per-window hotkey ids in 0x0000..0xBFFF. Starting away from zero keeps
    /// us clear of ids any other component in the same window might have chosen.
    /// </summary>
    private const int FirstHotkeyId = 0x4D42;

    private readonly Dictionary<string, Binding> _byId = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, Binding> _byHotkeyId = new();

    /// <summary>Assigned once per logical id, so rebinding a gesture reuses the same Win32 id.</summary>
    private readonly Dictionary<string, int> _hotkeyIds = new(StringComparer.OrdinalIgnoreCase);

    private readonly HwndSourceHook _hook;

    private HwndSource? _source;
    private IntPtr _hwnd;
    private int _nextHotkeyId = FirstHotkeyId;
    private bool _disposed;

    /// <summary>Creates a manager with no bindings and no window attached yet.</summary>
    public HotkeyManager()
    {
        // Cached so AddHook and RemoveHook are given the identical delegate instance.
        _hook = WndProcHook;
    }

    /// <summary>
    /// Why the last <see cref="Register"/> or <see cref="Attach"/> call failed, in words the
    /// user can act on, or null when the last call succeeded.
    /// </summary>
    public string? LastError { get; private set; }

    /// <summary>
    /// Binds hotkeys to <paramref name="window"/> and starts watching its messages. Safe to
    /// call before the window is shown, and safe to call again to move to another window.
    /// </summary>
    public void Attach(System.Windows.Window window)
    {
        if (_disposed || window is null) return;

        IntPtr handle;
        try
        {
            var helper = new WindowInteropHelper(window);
            handle = helper.Handle;
            if (handle == IntPtr.Zero) handle = helper.EnsureHandle();
        }
        catch (Exception ex)
        {
            LastError = $"Global hotkeys are unavailable: {ex.Message}";
            return;
        }

        if (handle == IntPtr.Zero)
        {
            LastError = "Global hotkeys are unavailable because the window has no handle yet.";
            return;
        }

        if (handle == _hwnd && _source is not null) return;

        // A hotkey belongs to the window it was registered against, so release them from the
        // old handle before adopting the new one.
        foreach (var binding in _byId.Values) Deactivate(binding);
        RemoveHook();

        var source = HwndSource.FromHwnd(handle);
        if (source is null)
        {
            LastError = "Global hotkeys are unavailable because the window does not expose a message hook.";
            return;
        }

        _hwnd = handle;
        _source = source;
        _source.AddHook(_hook);

        // Anything registered while there was no window becomes live now.
        foreach (var binding in _byId.Values) Activate(binding);
    }

    /// <summary>
    /// Binds <paramref name="gesture"/> (for example <c>"Ctrl+Alt+M"</c>) to
    /// <paramref name="callback"/> under the stable key <paramref name="id"/>, replacing any
    /// previous binding for that id. A null or empty gesture clears the binding and succeeds.
    /// </summary>
    /// <returns>
    /// False when the gesture cannot be parsed or Windows refuses it, which happens routinely
    /// because another application already owns the combination. <see cref="LastError"/> then
    /// explains it. Never throws.
    /// </returns>
    public bool Register(string id, string? gesture, Action callback)
    {
        LastError = null;

        if (_disposed)
        {
            LastError = "Hotkeys are no longer available.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(id))
        {
            LastError = "A hotkey needs an action name.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(gesture))
        {
            Unregister(id);
            return true;
        }

        if (callback is null)
        {
            LastError = "A hotkey needs an action to run.";
            return false;
        }

        var trimmed = gesture.Trim();
        if (!TryParseGesture(trimmed, out var modifiers, out var virtualKey, out var parseError))
        {
            LastError = parseError;
            return false;
        }

        foreach (var pair in _byId)
        {
            if (pair.Value.Modifiers == modifiers && pair.Value.VirtualKey == virtualKey
                && !string.Equals(pair.Key, id, StringComparison.OrdinalIgnoreCase))
            {
                LastError = $"{trimmed} is already assigned to another Mic Booster action.";
                return false;
            }
        }

        Unregister(id);

        var binding = new Binding(HotkeyIdFor(id), trimmed, modifiers, virtualKey, callback);
        _byId[id] = binding;
        _byHotkeyId[binding.HotkeyId] = binding;

        // Kept for Attach to apply. Reporting failure now would be a guess, since whether the
        // combination is free is only knowable once we own a window.
        if (_hwnd == IntPtr.Zero) return true;

        return Activate(binding);
    }

    /// <summary>Releases every hotkey. Ids stay reserved, so re-registering reuses them.</summary>
    public void UnregisterAll()
    {
        foreach (var binding in _byId.Values) Deactivate(binding);
        _byId.Clear();
        _byHotkeyId.Clear();
    }

    /// <summary>Releases every hotkey and stops watching the window's messages.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        UnregisterAll();
        RemoveHook();
    }

    private bool Activate(Binding binding)
    {
        if (binding.Active || _hwnd == IntPtr.Zero) return binding.Active;

        bool registered;
        int error;
        try
        {
            registered = RegisterHotKey(_hwnd, binding.HotkeyId, binding.Modifiers | ModNoRepeat, binding.VirtualKey);
            error = Marshal.GetLastWin32Error();
        }
        catch (Exception ex)
        {
            LastError = $"Could not register {binding.Gesture}: {ex.Message}";
            return false;
        }

        if (!registered)
        {
            LastError = error == ErrorHotkeyAlreadyRegistered
                ? $"{binding.Gesture} is already in use by another application."
                : $"Windows would not accept {binding.Gesture} (error {error}). Try a different combination.";
            return false;
        }

        binding.Active = true;
        return true;
    }

    private void Deactivate(Binding binding)
    {
        if (!binding.Active) return;
        binding.Active = false;

        if (_hwnd == IntPtr.Zero) return;
        try
        {
            UnregisterHotKey(_hwnd, binding.HotkeyId);
        }
        catch (Exception)
        {
            // The window may already be gone, in which case Windows has dropped the hotkey
            // for us. Either way there is nothing useful to do about it.
        }
    }

    private void Unregister(string id)
    {
        if (!_byId.TryGetValue(id, out var binding)) return;

        _byId.Remove(id);
        _byHotkeyId.Remove(binding.HotkeyId);
        Deactivate(binding);
    }

    private void RemoveHook()
    {
        var source = _source;
        _source = null;
        _hwnd = IntPtr.Zero;

        if (source is null) return;
        try
        {
            source.RemoveHook(_hook);
        }
        catch (Exception)
        {
            // The HwndSource belongs to the window and may already be disposed. Never dispose
            // it here, and never let teardown throw.
        }
    }

    private int HotkeyIdFor(string id)
    {
        if (_hotkeyIds.TryGetValue(id, out var hotkeyId)) return hotkeyId;

        hotkeyId = _nextHotkeyId++;
        _hotkeyIds[id] = hotkeyId;
        return hotkeyId;
    }

    private IntPtr WndProcHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WmHotkey) return IntPtr.Zero;

        var hotkeyId = unchecked((int)wParam.ToInt64());
        if (!_byHotkeyId.TryGetValue(hotkeyId, out var binding)) return IntPtr.Zero;

        handled = true;
        try
        {
            binding.Callback();
        }
        catch (Exception)
        {
            // We are inside the window's message loop; an exception from a handler would come
            // out as an unhandled exception and close the app.
        }

        return IntPtr.Zero;
    }

    private static bool TryParseGesture(string gesture, out uint modifiers, out uint virtualKey, out string? error)
    {
        modifiers = 0;
        virtualKey = 0;
        error = null;

        var parts = gesture.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string? keyToken = null;

        foreach (var part in parts)
        {
            switch (part.ToUpperInvariant())
            {
                case "CTRL":
                case "CTL":
                case "CONTROL":
                    modifiers |= ModControl;
                    break;
                case "ALT":
                case "MENU":
                    modifiers |= ModAlt;
                    break;
                case "SHIFT":
                    modifiers |= ModShift;
                    break;
                case "WIN":
                case "WINDOWS":
                case "SUPER":
                case "META":
                case "CMD":
                    modifiers |= ModWin;
                    break;
                default:
                    if (keyToken is not null)
                    {
                        error = $"'{gesture}' names more than one key. Use modifiers plus a single key.";
                        return false;
                    }
                    keyToken = part;
                    break;
            }
        }

        // "Ctrl++" means Ctrl and the + key; the separator swallowed the key above.
        if (keyToken is null && gesture.EndsWith("++", StringComparison.Ordinal)) keyToken = "PLUS";

        if (keyToken is null)
        {
            error = $"'{gesture}' has no key, only modifiers.";
            return false;
        }

        if (!TryResolveKey(keyToken, out virtualKey))
        {
            error = $"'{keyToken}' is not a key that can be used in a hotkey.";
            return false;
        }

        if (modifiers == 0 && !IsUsableWithoutModifier(virtualKey))
        {
            error = $"'{gesture}' needs Ctrl, Alt, Shift or Win as well, otherwise that key stops working everywhere else.";
            return false;
        }

        return true;
    }

    private static bool TryResolveKey(string token, out uint virtualKey)
    {
        virtualKey = 0;
        if (token.Length == 0) return false;

        // Single letters and digits are handled here because Enum.TryParse would read "5" as
        // the numeric enum value 5 instead of the 5 key.
        if (token.Length == 1)
        {
            var single = char.ToUpperInvariant(token[0]);
            if (single is (>= 'A' and <= 'Z') or (>= '0' and <= '9'))
            {
                // Virtual-key codes for A-Z and 0-9 are their ASCII values.
                virtualKey = single;
                return true;
            }
        }

        var upper = token.ToUpperInvariant();

        if (upper.Length is 2 or 3 && upper[0] == 'F'
            && int.TryParse(upper.AsSpan(1), out var functionKey)
            && functionKey is >= 1 and <= 24)
        {
            // VK_F1..VK_F24 are contiguous.
            virtualKey = (uint)Keys.F1 + (uint)(functionKey - 1);
            return true;
        }

        var named = upper switch
        {
            "ESC" or "ESCAPE" => Keys.Escape,
            "ENTER" or "RETURN" => Keys.Enter,
            "SPACE" or "SPACEBAR" => Keys.Space,
            "TAB" => Keys.Tab,
            "BACKSPACE" or "BACK" or "BKSP" => Keys.Back,
            "DEL" or "DELETE" => Keys.Delete,
            "INS" or "INSERT" => Keys.Insert,
            "HOME" => Keys.Home,
            "END" => Keys.End,
            "PGUP" or "PAGEUP" or "PRIOR" => Keys.PageUp,
            "PGDN" or "PAGEDOWN" or "NEXT" => Keys.PageDown,
            "UP" => Keys.Up,
            "DOWN" => Keys.Down,
            "LEFT" => Keys.Left,
            "RIGHT" => Keys.Right,
            "PAUSE" or "BREAK" => Keys.Pause,
            "PRTSC" or "PRINTSCREEN" or "SNAPSHOT" => Keys.PrintScreen,
            "SCROLL" or "SCROLLLOCK" => Keys.Scroll,
            "NUMLOCK" => Keys.NumLock,
            "CAPSLOCK" => Keys.CapsLock,
            "PLUS" => Keys.Oemplus,
            "MINUS" => Keys.OemMinus,
            "COMMA" => Keys.Oemcomma,
            "PERIOD" or "DOT" => Keys.OemPeriod,
            "SEMICOLON" => Keys.Oem1,
            "SLASH" => Keys.Oem2,
            "TILDE" or "GRAVE" or "BACKTICK" => Keys.Oem3,
            "OPENBRACKET" or "LEFTBRACKET" => Keys.Oem4,
            "BACKSLASH" => Keys.Oem5,
            "CLOSEBRACKET" or "RIGHTBRACKET" => Keys.Oem6,
            "QUOTE" or "APOSTROPHE" => Keys.Oem7,
            "NUMPLUS" or "NUMPADPLUS" => Keys.Add,
            "NUMMINUS" or "NUMPADMINUS" => Keys.Subtract,
            "NUMSTAR" or "NUMPADSTAR" => Keys.Multiply,
            "NUMSLASH" or "NUMPADSLASH" => Keys.Divide,
            _ => Keys.None
        };

        if (named != Keys.None)
        {
            virtualKey = (uint)named;
            return true;
        }

        // A bare number would parse as the enum's ordinal, which is not the key the user typed.
        if (IsAllDigits(upper)) return false;

        // Anything else the Keys enum knows by name, such as "NumPad5" or "MediaNextTrack".
        if (!Enum.TryParse<Keys>(token, ignoreCase: true, out var parsed)) return false;

        var code = (int)(parsed & Keys.KeyCode);
        if (code is <= 0 or > 0xFF) return false;
        if (IsModifierKeyCode(code)) return false;

        virtualKey = (uint)code;
        return true;
    }

    private static bool IsAllDigits(string token)
    {
        foreach (var c in token)
        {
            if (c is < '0' or > '9') return false;
        }

        return token.Length > 0;
    }

    /// <summary>
    /// Whether binding this key alone is reasonable. A bare hotkey is consumed system-wide, so
    /// only keys nobody types into a document are allowed without a modifier.
    /// </summary>
    private static bool IsUsableWithoutModifier(uint virtualKey)
    {
        if (virtualKey >= (uint)Keys.F1 && virtualKey <= (uint)Keys.F24) return true;
        return virtualKey == (uint)Keys.Pause
            || virtualKey == (uint)Keys.Scroll
            || virtualKey == (uint)Keys.PrintScreen;
    }

    private static bool IsModifierKeyCode(int code)
    {
        if (code >= (int)Keys.LShiftKey && code <= (int)Keys.RMenu) return true;
        return code == (int)Keys.ShiftKey
            || code == (int)Keys.ControlKey
            || code == (int)Keys.Menu
            || code == (int)Keys.LWin
            || code == (int)Keys.RWin;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private sealed class Binding
    {
        public Binding(int hotkeyId, string gesture, uint modifiers, uint virtualKey, Action callback)
        {
            HotkeyId = hotkeyId;
            Gesture = gesture;
            Modifiers = modifiers;
            VirtualKey = virtualKey;
            Callback = callback;
        }

        public int HotkeyId { get; }

        /// <summary>The gesture as the user wrote it, for error messages.</summary>
        public string Gesture { get; }

        public uint Modifiers { get; }
        public uint VirtualKey { get; }
        public Action Callback { get; }

        /// <summary>True once Windows has accepted the hotkey for the current window.</summary>
        public bool Active { get; set; }
    }
}
