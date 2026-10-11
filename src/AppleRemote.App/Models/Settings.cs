// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 AnthoDingo

using System.IO;
using System.Text.Json;

namespace AppleRemote.App.Models;

/// <summary>Preferences in %AppData%\AppleRemote\settings.json.</summary>
public sealed class Settings
{
    public bool KeyboardEnabled { get; set; }
    public string? KeyMapPath { get; set; }
    /// <summary>Closing the window minimises it to the notification area (otherwise the app exits).</summary>
    public bool CloseToTray { get; set; } = true;
    /// <summary>Remote input -> exe to launch (replaces the key).</summary>
    public Dictionary<string, string> Software { get; set; } = new();
    /// <summary>Remote input -> "#Win+D" combo to send (replaces the key).</summary>
    public Dictionary<string, string> Combos { get; set; } = new();
    /// <summary>Remote input -> name of the macro to run (file in the macros folder).</summary>
    public Dictionary<string, string> Macros { get; set; } = new();
    /// <summary>Software added through "Browse", kept in the menu.</summary>
    public List<string> Apps { get; set; } = new();
    /// <summary>"auto" (Windows language), "fr" or "en"; applied at startup.</summary>
    public string Language { get; set; } = "auto";
    /// <summary>Last release tag a toast was shown for (one notification per version).</summary>
    public string? LastNotifiedVersion { get; set; }

    public static string FilePath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AppleRemote", "settings.json");

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
        }
        catch
        {
            // unreadable file: defaults
        }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // non-critical preferences
        }
    }
}
