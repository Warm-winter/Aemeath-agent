using System.Runtime.InteropServices;

namespace Aemeath.Core.ComputerControl;

/// <summary>
/// Win32 输入执行层：用 SendInput 模拟鼠标点击、键盘输入、滚轮。
/// 这是「电脑控制 Agent」的动作执行器，移植自 UFO 的动作集（click_input / set_edit_text / keyboard_input / wheel_mouse_input）。
///
/// 所有方法都是真实的系统级模拟输入，会真的操控用户电脑，因此上层（ComputerControlAgent）
/// 必须走 ToolConfirmationService 的任务级前置确认。
/// </summary>
public static class InputExecutor
{
    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, int dwExtraInfo);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern short VkKeyScanW(char ch);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    private const uint MOUSEEVENTF_MOVE = 0x0001;
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    private const uint MOUSEEVENTF_RIGHTUP = 0x0010;
    private const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
    private const uint MOUSEEVENTF_MIDDLEUP = 0x0040;
    private const uint MOUSEEVENTF_WHEEL = 0x0800;
    private const uint MOUSEEVENTF_XDOWN = 0x0080;
    private const uint MOUSEEVENTF_XUP = 0x0100;

    private const uint INPUT_MOUSE = 0;
    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_UNICODE = 0x0004;
    private const uint WHEEL_DELTA = 120;

    /// <summary>在指定屏幕坐标点击鼠标。</summary>
    public static void Click(int x, int y, string button = "left", bool doubleClick = false)
    {
        SetCursorPos(x, y);
        Thread.Sleep(30);
        var (down, up) = button.ToLowerInvariant() switch
        {
            "right" => (MOUSEEVENTF_RIGHTDOWN, MOUSEEVENTF_RIGHTUP),
            "middle" => (MOUSEEVENTF_MIDDLEDOWN, MOUSEEVENTF_MIDDLEUP),
            _ => (MOUSEEVENTF_LEFTDOWN, MOUSEEVENTF_LEFTUP)
        };

        mouse_event(down, 0, 0, 0, 0);
        Thread.Sleep(20);
        mouse_event(up, 0, 0, 0, 0);

        if (doubleClick)
        {
            Thread.Sleep(30);
            mouse_event(down, 0, 0, 0, 0);
            Thread.Sleep(20);
            mouse_event(up, 0, 0, 0, 0);
        }
    }

    /// <summary>滚轮：wheelDist 正=向上，负=向下。</summary>
    public static void MouseWheel(int wheelDist)
    {
        // SetCursorPos 已由上层定位；这里直接发滚轮事件
        mouse_event(MOUSEEVENTF_WHEEL, 0, 0, (uint)(wheelDist * WHEEL_DELTA), 0);
    }

    /// <summary>输入文本（逐字符 Unicode 输入，避免键盘布局问题）。</summary>
    public static void TypeText(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        var inputs = new List<INPUT>(text.Length * 2);
        foreach (var ch in text)
        {
            inputs.Add(new INPUT
            {
                type = INPUT_KEYBOARD,
                u = new InputUnion
                {
                    ki = new KEYBDINPUT
                    {
                        wVk = 0,
                        wScan = ch,
                        dwFlags = KEYEVENTF_UNICODE,
                        time = 0,
                        dwExtraInfo = IntPtr.Zero
                    }
                }
            });
            inputs.Add(new INPUT
            {
                type = INPUT_KEYBOARD,
                u = new InputUnion
                {
                    ki = new KEYBDINPUT
                    {
                        wVk = 0,
                        wScan = ch,
                        dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP,
                        time = 0,
                        dwExtraInfo = IntPtr.Zero
                    }
                }
            });
        }

        SendInputArray(inputs);
    }

    /// <summary>
    /// 模拟快捷键/特殊键。keys 用 Win32 VkKeyScan 或 {VK_CONTROL}c 这种语法。
    /// 支持：普通字符（逐个 VkKeyScan）、{VK_XXX} 形式的虚拟键、{VK_XXX n} 表示按 n 次。
    /// </summary>
    public static void KeyboardInput(string keys)
    {
        if (string.IsNullOrEmpty(keys)) return;
        var inputs = new List<INPUT>();
        int i = 0;
        while (i < keys.Length)
        {
            if (keys[i] == '{')
            {
                var end = keys.IndexOf('}', i + 1);
                if (end > i)
                {
                    var token = keys[(i + 1)..end];
                    // {VK_X n} 表示连按 n 次
                    var parts = token.Split(' ');
                    var vkName = parts[0];
                    var repeat = parts.Length > 1 && int.TryParse(parts[1], out var r) ? r : 1;
                    var vkSpecial = ResolveVirtualKey(vkName);
                    for (int n = 0; n < repeat; n++)
                    {
                        AddKey(inputs, vkSpecial, false);
                        AddKey(inputs, vkSpecial, true);
                    }

                    i = end + 1;
                    continue;
                }
            }

            // 普通字符：用 VkKeyScan 得到虚拟键码（带 shift 修饰）
            var code = (ushort)VkKeyScanW(keys[i]);
            var vk = (byte)(code & 0xFF);
            var shift = (code & 0x100) != 0;
            if (shift)
            {
                AddKey(inputs, 0x10 /*VK_SHIFT*/, false);
            }

            AddKey(inputs, vk, false);
            AddKey(inputs, vk, true);
            if (shift)
            {
                AddKey(inputs, 0x10, true);
            }

            i++;
        }

        SendInputArray(inputs);
    }

    private static ushort ResolveVirtualKey(string name)
    {
        // 常用虚拟键名 → 码。可按需扩展。
        return name.ToUpperInvariant() switch
        {
            "VK_RETURN" or "ENTER" => 0x0D,
            "VK_ESCAPE" or "ESC" => 0x1B,
            "VK_TAB" or "TAB" => 0x09,
            "VK_BACK" or "BACKSPACE" => 0x08,
            "VK_SHIFT" => 0x10,
            "VK_CONTROL" or "VK_CTRL" or "CTRL" => 0x11,
            "VK_MENU" or "ALT" => 0x12,
            "VK_SPACE" or "SPACE" => 0x20,
            "VK_LEFT" => 0x25,
            "VK_UP" => 0x26,
            "VK_RIGHT" => 0x27,
            "VK_DOWN" => 0x28,
            "VK_DELETE" or "DEL" => 0x2E,
            "VK_HOME" => 0x24,
            "VK_END" => 0x23,
            "VK_F1" => 0x70,
            "VK_F2" => 0x71,
            "VK_F3" => 0x72,
            "VK_F4" => 0x73,
            "VK_F5" => 0x74,
            "WIN" or "VK_LWIN" => 0x5B,
            _ when name.Length == 1 && char.IsLetterOrDigit(name[0]) => (byte)char.ToUpper(name[0]),
            _ => 0x0D
        };
    }

    private static void AddKey(List<INPUT> inputs, ushort vk, bool up)
    {
        inputs.Add(new INPUT
        {
            type = INPUT_KEYBOARD,
            u = new InputUnion
            {
                ki = new KEYBDINPUT
                {
                    wVk = vk,
                    wScan = 0,
                    dwFlags = up ? KEYEVENTF_KEYUP : 0,
                    time = 0,
                    dwExtraInfo = IntPtr.Zero
                }
            }
        });
    }

    private static void SendInputArray(List<INPUT> inputs)
    {
        if (inputs.Count == 0) return;
        SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<INPUT>());
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion u;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

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

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }
}
