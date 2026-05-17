using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TermThing.Updater;

/// <summary>
/// Polls the GitHub <c>releases/latest</c> endpoint and returns information about
/// any available update, or <c>null</c> when the running version is already current,
/// the network is unavailable, or the current build is a dev build.
/// </summary>
public static class UpdateService
{
    // Replace these constants with the real owner/repo before tagging the first release.
    private const string Owner = "tomeko";
    private const string Repo  = "termthing";

    private static readonly HttpClient _http = CreateHttpClient();

    private static HttpClient CreateHttpClient()
    {
        var current = VersionHelper.CurrentVersion()?.ToString() ?? "0.0.0-dev";
        var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"TermThing/{current}");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        client.Timeout = TimeSpan.FromSeconds(15);
        return client;
    }

    /// <summary>
    /// Checks GitHub for a newer release.
    /// Returns <c>null</c> when no update is available, on network failure, or for dev builds.
    /// Never throws — all errors are swallowed and logged to <see cref="System.Diagnostics.Debug"/>.
    /// </summary>
    public static async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        var current = VersionHelper.CurrentVersion();
        if (current is null)
            return null;   // dev build — skip

        try
        {
            var url = $"https://api.github.com/repos/{Owner}/{Repo}/releases/latest";
            using var response = await _http.GetAsync(url, ct);

            if (response.StatusCode == System.Net.HttpStatusCode.Forbidden ||
                response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            {
                System.Diagnostics.Debug.WriteLine("[Updater] Rate-limited by GitHub; will retry next interval.");
                return null;
            }

            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(ct);
            var doc  = JsonNode.Parse(json)!;

            var tagName = doc["tag_name"]?.GetValue<string>() ?? string.Empty;
            var latest  = VersionHelper.ParseTag(tagName);
            if (latest is null || latest <= current)
                return null;

            var htmlUrl  = new Uri(doc["html_url"]?.GetValue<string>() ?? url);
            var body     = doc["body"]?.GetValue<string>() ?? string.Empty;

            var assetsJson = doc["assets"]?.AsArray() ?? [];
            var assets = assetsJson
                .Where(a => a is not null)
                .Select(a =>
                {
                    var name = a!["name"]?.GetValue<string>() ?? string.Empty;
                    var dlUrl = a["browser_download_url"]?.GetValue<string>() ?? string.Empty;
                    var size  = a["size"]?.GetValue<long>() ?? 0L;
                    return new UpdateAsset(name, new Uri(dlUrl), size);
                })
                .ToList();

            return new UpdateInfo(latest, tagName, body, htmlUrl, assets);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Updater] Check failed: {ex.Message}");
            return null;
        }
    }
}
