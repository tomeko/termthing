using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using TermThing.Diagnostics;

namespace TermThing.Updater;

/// <summary>
/// Reads the GitHub releases list: checks for an available update and fetches
/// release notes for the "What's new" and About dialogs. GitHub Releases are the
/// only changelog — there is no CHANGELOG file in the repo.
/// </summary>
public static class UpdateService
{
    private const string Owner = "tomeko";
    private const string Repo  = "termthing";

    public static Uri RepoUrl     { get; } = new($"https://github.com/{Owner}/{Repo}");
    public static Uri ReleasesUrl { get; } = new($"https://github.com/{Owner}/{Repo}/releases");

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

        var releases = await FetchReleasesAsync(ct);
        if (releases is null || releases.Count == 0)
            return null;

        var latest = releases.MaxBy(r => r.Note.Version)!;
        if (latest.Note.Version <= current)
            return null;

        var notes = releases
            .Select(r => r.Note)
            .Where(n => n.Version > current)
            .OrderByDescending(n => n.Version)
            .ToList();

        return new UpdateInfo(latest.Note.Version, latest.Note.TagName, notes,
            latest.Note.HtmlUrl, latest.Assets);
    }

    /// <summary>
    /// Notes for the releases in (<paramref name="after"/>, <paramref name="through"/>],
    /// newest first. A <c>null</c> <paramref name="after"/> means just
    /// <paramref name="through"/> itself. Returns <c>null</c> on network failure (so the
    /// caller can retry later) and an empty list when no matching release exists.
    /// </summary>
    public static async Task<IReadOnlyList<ReleaseNote>?> GetNotesAsync(
        Version? after, Version through, CancellationToken ct = default)
    {
        var releases = await FetchReleasesAsync(ct);
        if (releases is null)
            return null;

        return releases
            .Select(r => r.Note)
            .Where(n => after is null ? n.Version == through : n.Version > after && n.Version <= through)
            .OrderByDescending(n => n.Version)
            .ToList();
    }

    private sealed record Release(ReleaseNote Note, IReadOnlyList<UpdateAsset> Assets);

    /// <summary>
    /// Fetches the most recent published releases (drafts and pre-releases excluded,
    /// matching what <c>releases/latest</c> would consider). <c>null</c> on failure.
    /// </summary>
    private static async Task<List<Release>?> FetchReleasesAsync(CancellationToken ct)
    {
        try
        {
            var url = $"https://api.github.com/repos/{Owner}/{Repo}/releases?per_page=30";
            using var response = await _http.GetAsync(url, ct);

            if (response.StatusCode == System.Net.HttpStatusCode.Forbidden ||
                response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            {
                Log.Warn("updater", "Rate-limited by GitHub; will retry next interval.");
                return null;
            }

            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(ct);
            var list = JsonNode.Parse(json)?.AsArray() ?? [];

            var releases = new List<Release>();
            foreach (var doc in list)
            {
                if (doc is null) continue;
                if (doc["draft"]?.GetValue<bool>() == true) continue;
                if (doc["prerelease"]?.GetValue<bool>() == true) continue;

                var tagName = doc["tag_name"]?.GetValue<string>() ?? string.Empty;
                var version = VersionHelper.ParseTag(tagName);
                if (version is null) continue;

                var htmlUrl   = new Uri(doc["html_url"]?.GetValue<string>() ?? ReleasesUrl.ToString());
                var body      = doc["body"]?.GetValue<string>() ?? string.Empty;
                var published = DateTimeOffset.TryParse(doc["published_at"]?.GetValue<string>(), out var p)
                    ? p : (DateTimeOffset?)null;

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

                releases.Add(new Release(
                    new ReleaseNote(version, tagName, published, ReleaseNote.Clean(body), htmlUrl),
                    assets));
            }

            return releases;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            Log.Warn("updater", $"Release fetch failed: {ex.Message}");
            return null;
        }
    }
}
