// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 AnthoDingo

using System.ComponentModel;
using System.Runtime.InteropServices;
using static AppleRemote.Windows.Native.NativeMethods;

namespace AppleRemote.Windows;

/// <summary>
/// Virtual keyboard via <c>SendInput</c>: the Windows equivalent of /dev/uinput. No driver to install.
/// Limitation: Windows (UIPI) refuses to send keys to a window running as administrator
/// if this program isn't; run it as administrator in that case.
/// </summary>
public sealed class KeyboardInjector : IKeySink
{
    const uint MAPVK_VK_TO_VSC = 0;

    public string? LastError { get; private set; }

    public void Key(KeyCode key, bool down)
    {
        uint flags = down ? 0u : KEYEVENTF_KEYUP;
        if (key.Extended) flags |= KEYEVENTF_EXTENDEDKEY;

        var input = new INPUT
        {
            type = INPUT_KEYBOARD,
            U = new INPUT_UNION
            {
                ki = new KEYBDINPUT
                {
                    wVk = key.Vk,
                    wScan = (ushort)MapVirtualKey(key.Vk, MAPVK_VK_TO_VSC),
                    dwFlags = flags,
                    time = 0,
                    dwExtraInfo = IntPtr.Zero,
                },
            },
        };

        uint sent = SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
        LastError = sent == 1 ? null : new Win32Exception(Marshal.GetLastWin32Error()).Message;
    }
}
