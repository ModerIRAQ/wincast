using System.Diagnostics;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WinCast.Services;

internal sealed record UpdateInfo(
    Version CurrentVersion,
    Version LatestVersion,
    string TagName,
    string ReleaseUrl,
    string InstallerName,
    string InstallerDownloadUrl,
    bool IsUpdateAvailable);

internal static class UpdateService
{
    private const string LatestReleaseApiUrl = "https://api.github.com/repos/ModerIRAQ/wincast/releases/latest";
    internal static Version CurrentVersion { get; } = GetCurrentVersion();
    private static readonly HttpClient _httpClient = CreateHttpClient();

    internal static async Task<UpdateInfo> CheckForUpdatesAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.GetAsync(LatestReleaseApiUrl, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var release = await JsonSerializer.DeserializeAsync<GitHubRelease>(
            stream,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true },
            cancellationToken);

        if (release == null || string.IsNullOrWhiteSpace(release.TagName))
            throw new InvalidOperationException("GitHub did not return a valid latest release.");

        Version latestVersion = ParseVersion(release.TagName);
        GitHubAsset? installer = release.Assets
            .FirstOrDefault(asset =>
                asset.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
                asset.Name.Contains("setup", StringComparison.OrdinalIgnoreCase));

        if (installer == null)
            throw new InvalidOperationException("The latest release does not include a setup installer asset.");

        return new UpdateInfo(
            CurrentVersion,
            latestVersion,
            release.TagName,
            release.HtmlUrl,
            installer.Name,
            installer.BrowserDownloadUrl,
            latestVersion > CurrentVersion);
    }

    internal static async Task<string> DownloadInstallerAsync(UpdateInfo update, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        string safeName = string.Join("_", update.InstallerName.Split(Path.GetInvalidFileNameChars()));
        string targetDir = Path.Combine(Path.GetTempPath(), "WinCast", "Updates", update.TagName);
        Directory.CreateDirectory(targetDir);
        string targetPath = Path.Combine(targetDir, safeName);

        using var response = await _httpClient.GetAsync(update.InstallerDownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        long? total = response.Content.Headers.ContentLength;
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var destination = File.Create(targetPath);

        byte[] buffer = new byte[81920];
        long readTotal = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            readTotal += read;
            if (total is > 0)
                progress?.Report((double)readTotal / total.Value);
        }

        progress?.Report(1);
        return targetPath;
    }

    internal static void StartInstallerAndExit(string installerPath)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = installerPath,
            UseShellExecute = true
        });

        Environment.Exit(0);
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("WinCast", CurrentVersion.ToString()));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    private static Version GetCurrentVersion()
    {
        string? version = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;

        if (version != null)
        {
            int metadataIndex = version.IndexOf('+', StringComparison.Ordinal);
            if (metadataIndex >= 0)
                version = version[..metadataIndex];
        }

        return ParseVersion(version ?? "0.0.0");
    }

    private static Version ParseVersion(string value)
    {
        string normalized = value.Trim().TrimStart('v', 'V');
        int prereleaseIndex = normalized.IndexOf('-', StringComparison.Ordinal);
        if (prereleaseIndex >= 0)
            normalized = normalized[..prereleaseIndex];

        return Version.TryParse(normalized, out var version)
            ? version
            : new Version(0, 0, 0);
    }

    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")]
        public string TagName { get; set; } = "";

        [JsonPropertyName("html_url")]
        public string HtmlUrl { get; set; } = "";

        [JsonPropertyName("assets")]
        public List<GitHubAsset> Assets { get; set; } = new();
    }

    private sealed class GitHubAsset
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("browser_download_url")]
        public string BrowserDownloadUrl { get; set; } = "";
    }
}
