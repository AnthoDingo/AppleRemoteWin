using System.ComponentModel;
using System.Runtime.InteropServices;
using static AppleRemote.Windows.Native.NativeMethods;

namespace AppleRemote.Windows;

/// <summary>
/// Clavier virtuel via <c>SendInput</c> : l'équivalent Windows de /dev/uinput. Aucun pilote à installer.
/// Limite : Windows (UIPI) refuse d'envoyer des touches vers une fenêtre exécutée en administrateur
/// si ce programme ne l'est pas ; lancez-le en administrateur dans ce cas.
/// </summary>
public sealed class KeyboardInjector : IKeySink
{
    const uint MAPVK_VK_TO_VSC = 0, KEYEVENTF_UNICODE = 0x0004;

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

    /// <summary>Tape un texte caractère par caractère (événements Unicode : indépendant de la disposition du clavier).</summary>
    public void TypeText(string text)
    {
        foreach (char c in text)
            foreach (uint up in new[] { 0u, KEYEVENTF_KEYUP })
            {
                var input = new INPUT
                {
                    type = INPUT_KEYBOARD,
                    U = new INPUT_UNION { ki = new KEYBDINPUT { wVk = 0, wScan = c, dwFlags = KEYEVENTF_UNICODE | up } },
                };
                SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
            }
    }
}
