// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 AnthoDingo

using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using AppleRemote.App.Models;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace AppleRemote.App.Controls;

/// <summary>Block-based macro editor: Blockly in a WebView2, served from the Blockly folder next to the exe.
/// Shared by the Macros page and the macro editor window.</summary>
public sealed class MacroEditor : Border
{
    readonly WebView2 _web = new() { DefaultBackgroundColor = System.Drawing.Color.FromArgb(0x20, 0x20, 0x20) };
    bool _started, _ready;
    JsonNode? _pending;

    /// <summary>Software offered by the Software block.</summary>
    public IEnumerable<string> Software { get; set; } = [];

    public bool HasBlocks { get; private set; }

    /// <summary>The blocks changed (<see cref="HasBlocks"/> may have changed).</summary>
    public event EventHandler? Changed;

    /// <summary>WebView2 could not start (the user has already been told).</summary>
    public event EventHandler? Failed;

    public MacroEditor()
    {
        CornerRadius = new CornerRadius(6);
        BorderThickness = new Thickness(1);
        SetResourceReference(BorderBrushProperty, "ControlStrokeColorDefaultBrush");
        Child = _web;
        Loaded += OnLoaded;
    }

    async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_started) return; // Loaded fires again each time the page is shown
        _started = true;
        try
        {
            // One user data folder per exe: the app and the standalone editor cannot share a browser process (0x8007139F).
            string udf = Path.Combine(Path.GetDirectoryName(Settings.FilePath)!, AppDomain.CurrentDomain.FriendlyName + ".WebView2");
            var env = await CoreWebView2Environment.CreateAsync(null, udf);
            await _web.EnsureCoreWebView2Async(env);
            var core = _web.CoreWebView2;
            core.SetVirtualHostNameToFolderMapping("blockly.local", Path.Combine(AppContext.BaseDirectory, "Blockly"), CoreWebView2HostResourceAccessKind.Allow);
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            core.WebMessageReceived += OnMessage;
            string lang = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "fr" ? "fr" : "en";
            core.Navigate($"https://blockly.local/index.html?lang={lang}");
        }
        catch (Exception ex)
        {
            MessageBox.Show(Window.GetWindow(this), Loc.T("Macro.WebViewError", ex.Message), Loc.T("Macro.Title"), MessageBoxButton.OK, MessageBoxImage.Warning);
            Failed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Shows these blocks (null: empty workspace); deferred until Blockly is ready.</summary>
    public void Load(JsonNode? workspace)
    {
        _pending = workspace;
        if (_ready) _ = Run($"loadWorkspace({workspace?.ToJsonString() ?? "null"})");
    }

    public async Task<JsonNode?> GetWorkspaceAsync() =>
        JsonNode.Parse(JsonSerializer.Deserialize<string>(await Run("getWorkspace()"))!);

    async void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        using var doc = JsonDocument.Parse(e.WebMessageAsJson);
        var msg = doc.RootElement;
        switch (msg.GetProperty("type").GetString())
        {
            case "ready":
                _ready = true;
                Load(_pending);
                break;
            case "change":
                HasBlocks = msg.GetProperty("hasBlocks").GetBoolean();
                Changed?.Invoke(this, EventArgs.Empty);
                break;
            case "pickCombo":
                var dlg = new Views.KeyComboWindow { Owner = Window.GetWindow(this) };
                dlg.ShowDialog();
                if (dlg.Combo is { } combo) await SetField(msg.GetProperty("id").GetString()!, "COMBO", combo[KeyCombos.Prefix.Length..]);
                break;
            case "pickApp":
                PickApp(msg.GetProperty("id").GetString()!);
                break;
        }
    }

    Task<string> Run(string script) => _web.CoreWebView2.ExecuteScriptAsync(script);

    Task SetField(string id, string field, string value) =>
        Run($"setField({JsonSerializer.Serialize(id)},{JsonSerializer.Serialize(field)},{JsonSerializer.Serialize(value)})");

    /// <summary>Known software + Browse…; the choice fills the block's field.</summary>
    void PickApp(string blockId)
    {
        var menu = new ContextMenu { PlacementTarget = _web, Placement = PlacementMode.MousePoint };
        foreach (var path in Software)
        {
            var item = new MenuItem { Header = SoftwareApps.NameOf(path) };
            item.Click += async (_, _) => await SetField(blockId, "PATH", path);
            menu.Items.Add(item);
        }
        var browse = new MenuItem { Header = Loc.T("Pick.Browse") };
        browse.Click += async (_, _) =>
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "*.exe|*.exe" };
            if (dlg.ShowDialog(Window.GetWindow(this)) == true) await SetField(blockId, "PATH", dlg.FileName);
        };
        menu.Items.Add(browse);
        menu.IsOpen = true;
    }
}
