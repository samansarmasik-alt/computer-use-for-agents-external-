using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

internal sealed class InputController
{
    private const uint InputMouse = 0;
    private const uint InputKeyboard = 1;

    private const uint MouseEventLeftDown = 0x0002;
    private const uint MouseEventLeftUp = 0x0004;
    private const uint MouseEventRightDown = 0x0008;
    private const uint MouseEventRightUp = 0x0010;
    private const uint MouseEventMiddleDown = 0x0020;
    private const uint MouseEventMiddleUp = 0x0040;
    private const uint MouseEventXDown = 0x0080;
    private const uint MouseEventXUp = 0x0100;
    private const uint MouseEventVerticalWheel = 0x0800;
    private const uint MouseEventHorizontalWheel = 0x1000;
    private const uint KeyEventExtended = 0x0001;
    private const uint KeyEventKeyUp = 0x0002;
    private const uint KeyEventUnicode = 0x0004;
    private const int WheelDelta = 120;

    private readonly HashSet<ushort> _heldKeys = [];
    private readonly HashSet<MouseButton> _heldButtons = [];

    public Rectangle DesktopBounds => SystemInformation.VirtualScreen;

    public void ValidatePoint(int x, int y)
    {
        var bounds = DesktopBounds;
        if (bounds.Width <= 0 || bounds.Height <= 0 || !bounds.Contains(x, y))
        {
            throw new ArgumentOutOfRangeException(
                nameof(x),
                $"Point ({x}, {y}) is outside the current virtual desktop bounds ({bounds.Left}, {bounds.Top}, {bounds.Width}, {bounds.Height}).");
        }
    }

    public void Move(int x, int y)
    {
        ValidatePoint(x, y);
        if (!SetCursorPos(x, y))
        {
            throw CreateInputException("Windows could not move the cursor.");
        }
    }

    public void Click(int x, int y, string button, int clickCount)
    {
        if (clickCount is < 1 or > 3)
        {
            throw new ArgumentOutOfRangeException(nameof(clickCount), "clickCount must be between 1 and 3.");
        }

        var parsedButton = ParseMouseButton(button);
        Move(x, y);
        for (var click = 0; click < clickCount; click++)
        {
            HoldMouseButton(parsedButton);
            ReleaseMouseButton(parsedButton);

            if (click + 1 < clickCount)
            {
                Thread.Sleep(50);
            }
        }
    }

    public void Scroll(int x, int y, int verticalTicks, int horizontalTicks)
    {
        if (verticalTicks is < -10 or > 10 || horizontalTicks is < -10 or > 10)
        {
            throw new ArgumentOutOfRangeException(nameof(verticalTicks), "Scroll ticks must be between -10 and 10.");
        }

        Move(x, y);
        if (verticalTicks != 0)
        {
            SendMouseEvent(
                MouseEventVerticalWheel,
                unchecked((uint)(verticalTicks * WheelDelta)));
        }

        if (horizontalTicks != 0)
        {
            SendMouseEvent(
                MouseEventHorizontalWheel,
                unchecked((uint)(horizontalTicks * WheelDelta)));
        }
    }

    public void Drag(
        int startX,
        int startY,
        int endX,
        int endY,
        string button,
        int durationMs,
        CancellationToken cancellationToken)
    {
        if (durationMs is < 0 or > 3000)
        {
            throw new ArgumentOutOfRangeException(nameof(durationMs), "durationMs must be between 0 and 3000.");
        }

        ValidatePoint(startX, startY);
        ValidatePoint(endX, endY);
        var parsedButton = ParseMouseButton(button);

        Move(startX, startY);
        HoldMouseButton(parsedButton);

        var steps = Math.Max(1, (int)Math.Ceiling(durationMs / 16d));
        var delayPerStep = durationMs == 0 ? 0 : Math.Max(1, durationMs / steps);
        for (var step = 1; step <= steps; step++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (delayPerStep > 0)
            {
                cancellationToken.WaitHandle.WaitOne(delayPerStep);
                cancellationToken.ThrowIfCancellationRequested();
            }

            var progress = (double)step / steps;
            var x = (int)Math.Round(startX + ((endX - startX) * progress), MidpointRounding.AwayFromZero);
            var y = (int)Math.Round(startY + ((endY - startY) * progress), MidpointRounding.AwayFromZero);
            Move(x, y);
        }

        ReleaseMouseButton(parsedButton);
    }

    public string MouseButtonAction(string button, string action)
    {
        var parsedButton = ParseMouseButton(button);
        switch (Normalize(action))
        {
            case "HOLD":
            case "PRESS":
                HoldMouseButton(parsedButton);
                return $"Mouse button '{parsedButton}' is held.";
            case "RELEASE":
                return ReleaseMouseButton(parsedButton)
                    ? $"Mouse button '{parsedButton}' was released."
                    : $"Mouse button '{parsedButton}' was not held by PC Use.";
            default:
                throw new ArgumentException("Mouse button action must be 'press', 'hold', or 'release'.", nameof(action));
        }
    }

    public string KeyAction(string key, string action)
    {
        var keyInfo = ResolveKey(key);
        switch (Normalize(action))
        {
            case "PRESS":
                if (_heldKeys.Contains(keyInfo.VirtualKey))
                {
                    ReleaseKey(keyInfo);
                }

                HoldKey(keyInfo);
                ReleaseKey(keyInfo);
                return $"Key '{Normalize(key)}' was pressed.";
            case "HOLD":
                HoldKey(keyInfo);
                return $"Key '{Normalize(key)}' is held.";
            case "RELEASE":
                return ReleaseKey(keyInfo)
                    ? $"Key '{Normalize(key)}' was released."
                    : $"Key '{Normalize(key)}' was not held by PC Use.";
            default:
                throw new ArgumentException("Key action must be 'press', 'hold', or 'release'.", nameof(action));
        }
    }

    public void TypeText(string text, int maximumLength)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length > maximumLength)
        {
            throw new ArgumentOutOfRangeException(nameof(text), $"Text is limited to {maximumLength} UTF-16 code units.");
        }

        if (text.Contains('\0'))
        {
            throw new ArgumentException("Text cannot contain a null character.", nameof(text));
        }

        foreach (var codeUnit in text)
        {
            SendUnicodeUnit(codeUnit);
        }
    }

    public bool ReleaseKeyByName(string key)
    {
        return ReleaseKey(ResolveKey(key));
    }

    public bool ReleaseMouseButtonByName(string button)
    {
        return ReleaseMouseButton(ParseMouseButton(button));
    }

    public string ReleaseAll()
    {
        var released = 0;
        var failures = new List<string>();

        foreach (var key in _heldKeys.ToArray())
        {
            try
            {
                SendKeyboardEvent(new KeyInfo(key, IsExtendedVirtualKey(key)), isKeyUp: true);
                _heldKeys.Remove(key);
                released++;
            }
            catch (Exception exception)
            {
                failures.Add($"key 0x{key:X2}: {exception.Message}");
            }
        }

        foreach (var button in _heldButtons.ToArray())
        {
            try
            {
                SendMouseEvent(MouseUpEvent(button), MouseButtonData(button));
                _heldButtons.Remove(button);
                released++;
            }
            catch (Exception exception)
            {
                failures.Add($"mouse {button}: {exception.Message}");
            }
        }

        return failures.Count == 0
            ? $"Released {released} held input(s)."
            : $"Released {released} held input(s); cleanup failures: {string.Join("; ", failures)}";
    }

    public static MouseButton ParseMouseButton(string button)
    {
        return Normalize(button) switch
        {
            "LEFT" => MouseButton.Left,
            "RIGHT" => MouseButton.Right,
            "MIDDLE" => MouseButton.Middle,
            "X1" => MouseButton.X1,
            "X2" => MouseButton.X2,
            _ => throw new ArgumentException("Mouse button must be left, right, middle, x1, or x2.", nameof(button))
        };
    }

    public static void ValidateScroll(int verticalTicks, int horizontalTicks)
    {
        if (verticalTicks is < -10 or > 10 || horizontalTicks is < -10 or > 10)
        {
            throw new ArgumentOutOfRangeException(nameof(verticalTicks), "Scroll ticks must be between -10 and 10.");
        }
    }

    public static void ValidateClickCount(int clickCount)
    {
        if (clickCount is < 1 or > 3)
        {
            throw new ArgumentOutOfRangeException(nameof(clickCount), "clickCount must be between 1 and 3.");
        }
    }

    public static void ValidateDragDuration(int durationMs)
    {
        if (durationMs is < 0 or > 3000)
        {
            throw new ArgumentOutOfRangeException(nameof(durationMs), "durationMs must be between 0 and 3000.");
        }
    }

    public static void ValidateKey(string key)
    {
        _ = ResolveKey(key);
    }

    private void HoldMouseButton(MouseButton button)
    {
        if (_heldButtons.Contains(button))
        {
            return;
        }

        SendMouseEvent(MouseDownEvent(button), MouseButtonData(button));
        _heldButtons.Add(button);
    }

    private bool ReleaseMouseButton(MouseButton button)
    {
        if (!_heldButtons.Contains(button))
        {
            return false;
        }

        SendMouseEvent(MouseUpEvent(button), MouseButtonData(button));
        _heldButtons.Remove(button);
        return true;
    }

    private void HoldKey(KeyInfo key)
    {
        if (_heldKeys.Contains(key.VirtualKey))
        {
            return;
        }

        SendKeyboardEvent(key, isKeyUp: false);
        _heldKeys.Add(key.VirtualKey);
    }

    private bool ReleaseKey(KeyInfo key)
    {
        if (!_heldKeys.Contains(key.VirtualKey))
        {
            return false;
        }

        SendKeyboardEvent(key, isKeyUp: true);
        _heldKeys.Remove(key.VirtualKey);
        return true;
    }

    private static void SendUnicodeUnit(char codeUnit)
    {
        var down = new INPUT
        {
            Type = InputKeyboard,
            Data = new InputUnion
            {
                Keyboard = new KEYBDINPUT
                {
                    Scan = codeUnit,
                    Flags = KeyEventUnicode
                }
            }
        };
        var up = new INPUT
        {
            Type = InputKeyboard,
            Data = new InputUnion
            {
                Keyboard = new KEYBDINPUT
                {
                    Scan = codeUnit,
                    Flags = KeyEventUnicode | KeyEventKeyUp
                }
            }
        };

        var sent = SendInput(2, [down, up], Marshal.SizeOf<INPUT>());
        if (sent == 2)
        {
            return;
        }

        if (sent == 1)
        {
            _ = SendInput(1, [up], Marshal.SizeOf<INPUT>());
        }

        throw CreateInputException("Windows did not accept all Unicode keyboard events.");
    }

    private static void SendKeyboardEvent(KeyInfo key, bool isKeyUp)
    {
        var flags = key.Extended ? KeyEventExtended : 0;
        if (isKeyUp)
        {
            flags |= KeyEventKeyUp;
        }

        SendInputEvent(new INPUT
        {
            Type = InputKeyboard,
            Data = new InputUnion
            {
                Keyboard = new KEYBDINPUT
                {
                    VirtualKey = key.VirtualKey,
                    Flags = flags
                }
            }
        });
    }

    private static void SendMouseEvent(uint flags, uint data = 0)
    {
        SendInputEvent(new INPUT
        {
            Type = InputMouse,
            Data = new InputUnion
            {
                Mouse = new MOUSEINPUT
                {
                    MouseData = data,
                    Flags = flags
                }
            }
        });
    }

    private static void SendInputEvent(INPUT input)
    {
        var sent = SendInput(1, [input], Marshal.SizeOf<INPUT>());
        if (sent != 1)
        {
            throw CreateInputException("Windows did not accept the input event.");
        }
    }

    private static InvalidOperationException CreateInputException(string message)
    {
        var error = Marshal.GetLastWin32Error();
        return new InvalidOperationException($"{message} SendInput/Win32 error: {error}.");
    }

    private static uint MouseDownEvent(MouseButton button) => button switch
    {
        MouseButton.Left => MouseEventLeftDown,
        MouseButton.Right => MouseEventRightDown,
        MouseButton.Middle => MouseEventMiddleDown,
        MouseButton.X1 or MouseButton.X2 => MouseEventXDown,
        _ => throw new ArgumentOutOfRangeException(nameof(button))
    };

    private static uint MouseUpEvent(MouseButton button) => button switch
    {
        MouseButton.Left => MouseEventLeftUp,
        MouseButton.Right => MouseEventRightUp,
        MouseButton.Middle => MouseEventMiddleUp,
        MouseButton.X1 or MouseButton.X2 => MouseEventXUp,
        _ => throw new ArgumentOutOfRangeException(nameof(button))
    };

    private static uint MouseButtonData(MouseButton button) => button switch
    {
        MouseButton.X1 => 1,
        MouseButton.X2 => 2,
        _ => 0
    };

    private static KeyInfo ResolveKey(string key)
    {
        var normalized = Normalize(key);
        if (KeyMap.TryGetValue(normalized, out var keyInfo))
        {
            return keyInfo;
        }

        throw new ArgumentException(
            $"Unsupported key '{key}'. Use a letter, digit, F1-F24, or a documented key name such as ENTER, ESC, CTRL, SHIFT, ALT, TAB, SPACE, LEFT, or DELETE.",
            nameof(key));
    }

    private static string Normalize(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.Trim().ToUpperInvariant();
    }

    private static bool IsExtendedVirtualKey(ushort virtualKey) =>
        virtualKey is 0x21 or 0x22 or 0x23 or 0x24 or 0x25 or 0x26 or 0x27 or 0x28 or 0x2D or 0x2E or 0x5B or 0x5C or 0x5D or 0x6F or 0x90 or 0xA3;

    private static Dictionary<string, KeyInfo> BuildKeyMap()
    {
        var keys = new Dictionary<string, KeyInfo>(StringComparer.OrdinalIgnoreCase)
        {
            ["BACKSPACE"] = new(0x08, false),
            ["TAB"] = new(0x09, false),
            ["ENTER"] = new(0x0D, false),
            ["RETURN"] = new(0x0D, false),
            ["SHIFT"] = new(0x10, false),
            ["CTRL"] = new(0x11, false),
            ["CONTROL"] = new(0x11, false),
            ["ALT"] = new(0x12, false),
            ["PAUSE"] = new(0x13, false),
            ["CAPSLOCK"] = new(0x14, false),
            ["ESC"] = new(0x1B, false),
            ["ESCAPE"] = new(0x1B, false),
            ["SPACE"] = new(0x20, false),
            ["PAGEUP"] = new(0x21, true),
            ["PAGEDOWN"] = new(0x22, true),
            ["END"] = new(0x23, true),
            ["HOME"] = new(0x24, true),
            ["LEFT"] = new(0x25, true),
            ["UP"] = new(0x26, true),
            ["RIGHT"] = new(0x27, true),
            ["DOWN"] = new(0x28, true),
            ["PRINTSCREEN"] = new(0x2C, true),
            ["INSERT"] = new(0x2D, true),
            ["DELETE"] = new(0x2E, true),
            ["WIN"] = new(0x5B, true),
            ["WINDOWS"] = new(0x5B, true),
            ["APPS"] = new(0x5D, true),
            ["NUMLOCK"] = new(0x90, true),
            ["SCROLLLOCK"] = new(0x91, false),
            ["NUMPAD0"] = new(0x60, false),
            ["NUMPAD1"] = new(0x61, false),
            ["NUMPAD2"] = new(0x62, false),
            ["NUMPAD3"] = new(0x63, false),
            ["NUMPAD4"] = new(0x64, false),
            ["NUMPAD5"] = new(0x65, false),
            ["NUMPAD6"] = new(0x66, false),
            ["NUMPAD7"] = new(0x67, false),
            ["NUMPAD8"] = new(0x68, false),
            ["NUMPAD9"] = new(0x69, false),
            ["MULTIPLY"] = new(0x6A, false),
            ["ADD"] = new(0x6B, false),
            ["SUBTRACT"] = new(0x6D, false),
            ["DECIMAL"] = new(0x6E, false),
            ["DIVIDE"] = new(0x6F, true),
            ["OEM_PLUS"] = new(0xBB, false),
            ["OEM_COMMA"] = new(0xBC, false),
            ["OEM_MINUS"] = new(0xBD, false),
            ["OEM_PERIOD"] = new(0xBE, false),
            ["OEM_2"] = new(0xBF, false),
            ["OEM_1"] = new(0xBA, false),
            ["OEM_4"] = new(0xDB, false),
            ["OEM_5"] = new(0xDC, false),
            ["OEM_6"] = new(0xDD, false),
            ["OEM_7"] = new(0xDE, false),
            ["OEM_3"] = new(0xC0, false)
        };

        for (var character = 'A'; character <= 'Z'; character++)
        {
            keys[character.ToString()] = new((ushort)character, false);
        }

        for (var character = '0'; character <= '9'; character++)
        {
            keys[character.ToString()] = new((ushort)character, false);
        }

        for (var functionKey = 1; functionKey <= 24; functionKey++)
        {
            keys[$"F{functionKey}"] = new((ushort)(0x70 + functionKey - 1), false);
        }

        return keys;
    }

    private static readonly Dictionary<string, KeyInfo> KeyMap = BuildKeyMap();

    private readonly record struct KeyInfo(ushort VirtualKey, bool Extended);

    public enum MouseButton
    {
        Left,
        Right,
        Middle,
        X1,
        X2
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint Type;
        public InputUnion Data;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)]
        public MOUSEINPUT Mouse;

        [FieldOffset(0)]
        public KEYBDINPUT Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int Dx;
        public int Dy;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort VirtualKey;
        public ushort Scan;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, [In] INPUT[] inputs, int size);
}
