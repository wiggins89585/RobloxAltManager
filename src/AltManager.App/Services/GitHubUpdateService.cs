using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AltManager.App.Services;

public sealed class GitHubUpdateService
{
    private const string ReleaseApiUrl = "https://api.github.com/repos/wiggins89585/RobloxAltManager/releases/latest";
    private static readonly HttpClient Http = CreateHttpClient();

    public async Task<AvailableUpdate?> CheckForUpdateAsync(Version currentVersion, CancellationToken cancellationToken)
    {
        using var response = await Http.GetAsync(ReleaseApiUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var releaseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var release = await JsonSerializer.DeserializeAsync<GitHubRelease>(releaseStream, cancellationToken: cancellationToken)
            ?? throw new InvalidDataException("GitHub returned an empty release.");

        if (!TryParseVersion(release.TagName, out var releaseVersion) ||
            releaseVersion <= NormalizeVersion(currentVersion))
            return null;

        var assetName = $"RobloxAltManager-v{releaseVersion.ToString(3)}-win-x64.zip";
        var asset = release.Assets.FirstOrDefault(candidate => candidate.Name == assetName)
            ?? throw new InvalidDataException($"Release {release.TagName} has no {assetName} package.");
        if (!Uri.TryCreate(asset.DownloadUrl, UriKind.Absolute, out var downloadUri) ||
            downloadUri.Scheme != Uri.UriSchemeHttps ||
            !string.Equals(downloadUri.Host, "github.com", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("GitHub release contains an unexpected package URL.");
        if (asset.Digest is not { } digest || !digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ||
            digest.Length != 71)
            throw new InvalidDataException("GitHub release has no valid SHA-256 package digest.");

        byte[] expectedDigest;
        try
        {
            expectedDigest = Convert.FromHexString(digest["sha256:".Length..]);
        }
        catch (FormatException ex)
        {
            throw new InvalidDataException("GitHub release has an invalid SHA-256 digest.", ex);
        }
        if (expectedDigest.Length != 32)
            throw new InvalidDataException("GitHub release has an invalid SHA-256 digest.");

        return new AvailableUpdate(releaseVersion, assetName, downloadUri, digest);
    }

    public async Task<PreparedUpdate> DownloadAsync(AvailableUpdate update, CancellationToken cancellationToken)
    {
        var updatesRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AltManager", "Updates");
        var updateDirectory = Path.Combine(updatesRoot, Guid.NewGuid().ToString("N"));
        var packagePath = Path.Combine(updateDirectory, update.AssetName);
        var stagingDirectory = Path.Combine(updateDirectory, "staged");
        Directory.CreateDirectory(stagingDirectory);

        try
        {
            using var packageResponse = await DownloadPackageAsync(update.DownloadUri, cancellationToken);
            await using var source = await packageResponse.Content.ReadAsStreamAsync(cancellationToken);
            await using var destination = new FileStream(packagePath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await source.CopyToAsync(destination, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException($"Could not download the update package: {ex.Message}", ex);
        }

        await using var packageStream = File.OpenRead(packagePath);
        var actualDigest = await SHA256.HashDataAsync(packageStream, cancellationToken);
        var expectedDigest = Convert.FromHexString(update.Digest["sha256:".Length..]);
        if (!CryptographicOperations.FixedTimeEquals(actualDigest, expectedDigest))
            throw new InvalidDataException("Downloaded update package failed its SHA-256 check.");

        ZipFile.ExtractToDirectory(packagePath, stagingDirectory);
        if (!File.Exists(Path.Combine(stagingDirectory, "AltManager.exe")))
            throw new InvalidDataException("Update package does not contain AltManager.exe.");

        return new PreparedUpdate(update.Version, updateDirectory, stagingDirectory);
    }

    public Process StartApply(PreparedUpdate update, bool usePasswordVault)
    {
        var installDirectory = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
        var writeProbe = Path.Combine(installDirectory, $".update-check-{Guid.NewGuid():N}");
        try
        {
            using (File.Create(writeProbe)) { }
            File.Delete(writeProbe);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new InvalidOperationException("The install folder is not writable. Move the app to a writable folder to enable automatic updates.", ex);
        }

        var executablePath = Path.Combine(installDirectory, "AltManager.exe");
        var updatesRoot = Path.GetDirectoryName(update.UpdateDirectory)!;
        var scriptPath = Path.Combine(updatesRoot, "ApplyUpdate.ps1");
        File.WriteAllText(scriptPath, ApplyScript);

        var powershellPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe");
        var startInfo = new ProcessStartInfo(powershellPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = updatesRoot
        };
        foreach (var argument in new[]
        {
            "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", scriptPath,
            Environment.ProcessId.ToString(), installDirectory, update.StagingDirectory, update.UpdateDirectory,
            executablePath, usePasswordVault.ToString()
        })
            startInfo.ArgumentList.Add(argument);

        return Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start the update helper.");
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("RobloxAltManager", "1.0"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    private static async Task<HttpResponseMessage> DownloadPackageAsync(Uri downloadUri, CancellationToken cancellationToken)
    {
        try
        {
            var response = await Http.GetAsync(downloadUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            return response;
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException($"Could not download the update package: {ex.Message}", ex);
        }
    }

    private static bool TryParseVersion(string? tagName, out Version version)
    {
        version = new Version(0, 0);
        if (tagName is null || !Version.TryParse(tagName.TrimStart('v', 'V'), out var parsed))
            return false;
        version = NormalizeVersion(parsed);
        return true;
    }

    private static Version NormalizeVersion(Version version) =>
        new(version.Major, version.Minor, Math.Max(version.Build, 0), Math.Max(version.Revision, 0));

    private const string ApplyScript = """
        param(
            [int]$ParentProcessId,
            [string]$InstallDirectory,
            [string]$StagingDirectory,
            [string]$UpdateDirectory,
            [string]$ExecutablePath,
            [bool]$UsePasswordVault
        )

        $ErrorActionPreference = 'Stop'
        try {
            while (Get-Process -Id $ParentProcessId -ErrorAction SilentlyContinue) {
                Start-Sleep -Milliseconds 500
            }

            Get-ChildItem -LiteralPath $StagingDirectory -Force | ForEach-Object {
                Copy-Item -LiteralPath $_.FullName -Destination $InstallDirectory -Recurse -Force
            }

            if ($UsePasswordVault) {
                Start-Process -FilePath $ExecutablePath -WorkingDirectory $InstallDirectory -ArgumentList '--password-vault'
            } else {
                Start-Process -FilePath $ExecutablePath -WorkingDirectory $InstallDirectory
            }

            Remove-Item -LiteralPath $UpdateDirectory -Recurse -Force
        } catch {
            Add-Type -AssemblyName PresentationFramework
            [System.Windows.MessageBox]::Show(
                "Update failed: $($_.Exception.Message)`n`nDownload the latest release manually from GitHub.",
                'Roblox Alt Manager update failed',
                [System.Windows.MessageBoxButton]::OK,
                [System.Windows.MessageBoxImage]::Error
            ) | Out-Null
        }
        """;

    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")]
        public string TagName { get; set; } = "";

        [JsonPropertyName("assets")]
        public List<GitHubAsset> Assets { get; set; } = new();
    }

    private sealed class GitHubAsset
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("browser_download_url")]
        public string DownloadUrl { get; set; } = "";

        [JsonPropertyName("digest")]
        public string? Digest { get; set; }
    }
}

public sealed record PreparedUpdate(Version Version, string UpdateDirectory, string StagingDirectory);
public sealed record AvailableUpdate(Version Version, string AssetName, Uri DownloadUri, string Digest);
