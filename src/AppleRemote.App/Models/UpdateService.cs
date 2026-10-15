// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 AnthoDingo

using System.Diagnostics;
using System.IO;
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
    readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(5) };

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

    /// <summary>A GitHub release; <paramref name="SetupUrl"/> and <paramref name="Sha256"/> are set only if the matching setup asset publishes a digest.</summary>
    public sealed record Release(string Tag, string Url, bool Newer, string? SetupUrl, string? Sha256);

    const string DownloadPrefix = $"https://github.com/{Repo}/releases/download/";

    public static async Task<Release> LatestAsync(HttpClient http, CancellationToken ct = default)
    {
        using var json = JsonDocument.Parse(await http.GetStringAsync($"https://api.github.com/repos/{Repo}/releases/latest", ct));
        var root = json.RootElement;
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        var url = root.GetProperty("html_url").GetString() ?? $"https://github.com/{Repo}/releases";
        var current = typeof(UpdateService).Assembly.GetName().Version!;
        bool newer = Version.TryParse(tag.TrimStart('v', 'V'), out var latest) && latest > current;

        string arch = $"win-{System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()}";
        string? setupUrl = null, sha = null;
        if (root.TryGetProperty("assets", out var assets))
            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.GetProperty("name").GetString() ?? "";
                var link = asset.GetProperty("browser_download_url").GetString() ?? "";
                var digest = asset.TryGetProperty("digest", out var d) ? d.GetString() : null;
                if (!name.EndsWith($"{arch}-setup.exe", StringComparison.OrdinalIgnoreCase) || !link.StartsWith(DownloadPrefix, StringComparison.Ordinal)
                    || digest is null || !digest.StartsWith("sha256:", StringComparison.Ordinal)) continue;
                setupUrl = link;
                sha = digest["sha256:".Length..];
                break;
            }
        return new(tag, url, newer, setupUrl, sha);
    }

    /// <summary>Tag of the latest release of another repository (the dongle firmware), without a leading "v".</summary>
    public static async Task<string> LatestTagAsync(HttpClient http, string repo, CancellationToken ct = default)
    {
        using var json = JsonDocument.Parse(await http.GetStringAsync($"https://api.github.com/repos/{repo}/releases/latest", ct));
        return (json.RootElement.GetProperty("tag_name").GetString() ?? "").TrimStart('v', 'V');
    }

    /// <summary>Raised (any thread) after the background poll has recorded a check.</summary>
    public static event Action? Checked;

    static bool _installing;

    /// <summary>
    /// Downloads the release's setup, checks it against the digest GitHub publishes for the asset, then runs it silently
    /// (UAC prompt: the setup needs administrator rights) and relaunches the app. The app exits so the setup can replace its files.
    /// False if nothing was installed (no verifiable asset, download or digest failure).
    /// </summary>
    public static async Task<bool> InstallAsync(HttpClient http, Release release, Action<string> log, CancellationToken ct = default)
    {
        if (release.SetupUrl is null || release.Sha256 is null || _installing) return false;
        _installing = true;
        try
        {
            var path = Path.Combine(Path.GetTempPath(), $"AppleRemote-update-{release.Tag}.exe");
            await using (var file = File.Create(path))
            {
                using var response = await http.GetAsync(release.SetupUrl, HttpCompletionOption.ResponseHeadersRead, ct);
                response.EnsureSuccessStatusCode();
                await (await response.Content.ReadAsStreamAsync(ct)).CopyToAsync(file, ct);
            }
            string actual = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(path, ct)));
            if (!actual.Equals(release.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(path);
                log($"Update {release.Tag} rejected: SHA-256 mismatch");
                return false;
            }

            log($"Installing {release.Tag}");
            var exe = Environment.ProcessPath!;
            // `start /wait` lets cmd raise the setup's UAC prompt; the app relaunches once the setup is done.
            Process.Start(new ProcessStartInfo("cmd.exe", $"/c start /wait \"\" \"{path}\" /SILENT /SUPPRESSMSGBOXES /NORESTART & start \"\" \"{exe}\"")
                { CreateNoWindow = true, UseShellExecute = false });
            System.Windows.Application.Current.Dispatcher.Invoke(System.Windows.Application.Current.Shutdown);
            return true;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log($"Update {release.Tag} failed: {e.Message}");
            return false;
        }
        finally { _installing = false; }
    }

    async Task CheckAsync()
    {
        try
        {
            var release = await LatestAsync(_http, _cts.Token);
            settings.LastUpdateCheck = DateTime.Now;
            settings.Save();
            Checked?.Invoke();
            if (!release.Newer) return;
            if (settings.AutoUpdate && await InstallAsync(_http, release, log, _cts.Token)) return;
            if (!settings.NotifyUpdates || settings.LastNotifiedVersion == release.Tag) return;

            settings.LastNotifiedVersion = release.Tag;
            settings.Save();
            log($"Update available: {release.Tag} ({release.Url})");
            Notify(release.Tag, release.Url);
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
