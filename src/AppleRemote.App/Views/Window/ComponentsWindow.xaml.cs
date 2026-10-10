// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 AnthoDingo

namespace AppleRemote.App.Views;

public partial class ComponentsWindow : Wpf.Ui.Controls.FluentWindow
{
    record Component(string Name, string Version, string License, string Usage, string Url);

    // Keep in sync with the PackageReference items of AppleRemote.App.csproj and the Cargo.toml of the driver.
    static readonly Component[] Components =
    [
        new(".NET / WPF", "10", "MIT", "Runtime and UI framework", "https://github.com/dotnet/wpf"),
        new("WPF-UI (+ Tray)", "4.3.0", "MIT", "Fluent controls, navigation, tray icon", "https://github.com/lepoco/wpfui"),
        new("CommunityToolkit.Mvvm", "8.4.2", "MIT", "MVVM source generators", "https://github.com/CommunityToolkit/dotnet"),
        new("open-appleremote", "", "MIT", "Decoder and gestures (C# port), protocol reference", "https://github.com/a-funk/open-appleremote"),
        new("windows-drivers-rs (wdk, wdk-sys, wdk-alloc, wdk-panic, wdk-build)", "0.3.0", "MIT OR Apache-2.0", "Kernel filter driver", "https://github.com/microsoft/windows-drivers-rs"),
        new("Bumble", "", "Apache-2.0", "Bluetooth probe (tools/siri_bumble.py only)", "https://github.com/google/bumble"),
    ];

    public ComponentsWindow()
    {
        InitializeComponent();
        List.ItemsSource = Components;
    }
}
