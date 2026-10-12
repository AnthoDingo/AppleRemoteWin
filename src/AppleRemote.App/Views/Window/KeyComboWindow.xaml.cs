using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using AppleRemote.App.Models;

namespace AppleRemote.App.Views;

/// <summary>
/// Saisie d'une combinaison de touches. Un hook clavier bas niveau (actif seulement tant que la fenêtre est au premier plan)
/// capte aussi les raccourcis que Windows garderait pour lui (Win+D, Alt+Tab, Alt+F4) et les avale.
/// Fermer la fenêtre sans valider laisse <see cref="Combo"/> à null.
/// </summary>
public partial class KeyComboWindow : Wpf.Ui.Controls.FluentWindow
{
    delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    struct KbdHook { public uint Vk, Scan, Flags, Time; public IntPtr Extra; }

    [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr module, uint thread);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string? name);

    const int WH_KEYBOARD_LL = 13, WM_KEYDOWN = 0x100, WM_KEYUP = 0x101, WM_SYSKEYDOWN = 0x104, WM_SYSKEYUP = 0x105;
    const uint LLKHF_INJECTED = 0x10;

    readonly HookProc _proc; // gardé en champ : sinon le ramasse-miettes libère le délégué appelé par Windows
    readonly HashSet<ushort> _held = new();
    readonly HashSet<string> _mods = new();
    string? _key;
    IntPtr _hook;

    /// <summary>Valeur « #Win+D » validée, null si annulée.</summary>
    public string? Combo { get; private set; }

    public KeyComboWindow()
    {
        InitializeComponent();
        _proc = OnKey;
    }

    void Window_Loaded(object sender, RoutedEventArgs e)
    {
        using var m = Process.GetCurrentProcess().MainModule!;
        _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(m.ModuleName), 0);
    }

    void Window_Closed(object? sender, EventArgs e)
    {
        if (_hook != IntPtr.Zero) UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }

    IntPtr OnKey(int code, IntPtr wParam, IntPtr lParam)
    {
        var k = Marshal.PtrToStructure<KbdHook>(lParam);
        if (code < 0 || !IsActive || (k.Flags & LLKHF_INJECTED) != 0) return CallNextHookEx(_hook, code, wParam, lParam);

        int msg = (int)wParam;
        ushort vk = (ushort)k.Vk;
        if (msg is WM_KEYDOWN or WM_SYSKEYDOWN)
        {
            if (_held.Count == 0) Clear();  // nouvelle combinaison dès que tout a été relâché
            if (_held.Add(vk)) Add(vk);
        }
        else if (msg is WM_KEYUP or WM_SYSKEYUP) _held.Remove(vk);
        return 1; // avalé : Windows ne voit rien
    }

    void Add(ushort vk)
    {
        if (KeyCombos.ModifierName(vk) is { } mod) _mods.Add(mod);
        else if (KeyCombos.KeyName(vk) is { } name) _key = name;
        Refresh();
    }

    void Clear() { _mods.Clear(); _key = null; Refresh(); }

    void Refresh()
    {
        string c = KeyCombos.Build(_mods, _key);
        bool any = c.Length > KeyCombos.Prefix.Length;
        ComboText.Text = any ? KeyCombos.Display(c) : "";
        ValidateButton.IsEnabled = any;
    }

    void Reset_Click(object sender, RoutedEventArgs e) { _held.Clear(); Clear(); }

    void Validate_Click(object sender, RoutedEventArgs e)
    {
        Combo = KeyCombos.Build(_mods, _key);
        Close();
    }
}
