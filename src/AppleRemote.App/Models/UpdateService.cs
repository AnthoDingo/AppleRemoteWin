// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 AnthoDingo

using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using Microsoft.Win32;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace AppleRemote.App.Models;

/// <summary>
/// Polls the latest GitHub release and shows a Windows toast once per new version.
/// Network or parsing failures are silent: updates are a convenience, never an error.
/// </summary>
public sealed class UpdateService(Settings settings, Action<string> log) : IDisposable
{
    const string Repo = "AnthoDingo/AppleRemoteWin";
    const string AppId = "AppleRemote";
    static readonly TimeSpan Interval = TimeSpan.FromHours(6);

    readonly CancellationTokenSource _cts = new();
    readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };

    public void Start()
    {
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("AppleRemote-UpdateCheck");
        _ = Task.Run(async () =>
        {
            while (!_cts.IsCancellationRequested)
            {
                await CheckAsync();
                try { await Task.Delay(Interval, _cts.Token); } catch (OperationCanceledException) { }
            }
        });
    }

    async Task CheckAsync()
    {
        try
        {
            using var json = JsonDocument.Parse(await _http.GetStringAsync($"https://api.github.com/repos/{Repo}/releases/latest", _cts.Token));
            var tag = json.RootElement.GetProperty("tag_name").GetString() ?? "";
            var url = json.RootElement.GetProperty("html_url").GetString() ?? $"https://github.com/{Repo}/releases";
            var current = typeof(UpdateService).Assembly.GetName().Version!;
            if (!Version.TryParse(tag.TrimStart('v', 'V'), out var latest) || latest <= current || settings.LastNotifiedVersion == tag) return;

            settings.LastNotifiedVersion = tag;
            settings.Save();
            log($"Update available: {tag} ({url})");
            Notify(tag, url);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // offline, rate-limited, no release yet: try again at the next interval
        }
    }

    static void Notify(string tag, string url)
    {
        // An unpackaged app needs an AppUserModelID registered under HKCU for its toasts to be displayed.
        using (var key = Registry.CurrentUser.CreateSubKey($@"Software\Classes\AppUserModelId\{AppId}"))
            key.SetValue("DisplayName", "Siri Remote");

        var xml = new XmlDocument();
        xml.LoadXml($"<toast><visual><binding template=\"ToastGeneric\"><text>{Loc.T("Update.Title")}</text><text>{System.Security.SecurityElement.Escape(Loc.T("Update.Body", tag))}</text></binding></visual></toast>");
        var toast = new ToastNotification(xml);
        // Fired in-process while the app runs (it lives in the notification area).
        toast.Activated += (_, _) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        ToastNotificationManager.CreateToastNotifier(AppId).Show(toast);
    }

    public void Dispose()
    {
        _cts.Cancel();
        _http.Dispose();
    }
}
