using System.Globalization;
using System.Net.Http;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Questyle.Desktop.Services;

public sealed record InstalledVersion(Version Version, long BuildNumber, DateTimeOffset BuiltAt)
{
    public static InstalledVersion Current
    {
        get
        {
            var assembly = typeof(InstalledVersion).Assembly;
            var metadata = assembly.GetCustomAttributes<AssemblyMetadataAttribute>().ToDictionary(value => value.Key, value => value.Value);
            return new(assembly.GetName().Version!,
                metadata.TryGetValue("DevBuildNumber", out var build) ? long.Parse(build!, CultureInfo.InvariantCulture) : 0,
                metadata.TryGetValue("BuildTimestamp", out var timestamp) ? DateTimeOffset.Parse(timestamp!, CultureInfo.InvariantCulture) : DateTimeOffset.MinValue);
        }
    }

    // CI writes a build number only into development builds.
    public UpdateChannel Channel => BuildNumber > 0 ? UpdateChannel.Development : UpdateChannel.Stable;
}

// Package is the GitHub asset API address; a null package marks the developer simulation.
public sealed record UpdateRelease(Version Version, long BuildNumber, DateTimeOffset PublishedAt, Uri? Package = null, long PackageSize = 0, string PackageSha256 = "")
{
    public bool Development => BuildNumber > 0;
    public bool Simulated => Package == null;
    public string Label => Development ? "Dev build " + BuildNumber.ToString(CultureInfo.InvariantCulture) : Version.ToString(3);
    public string DisplayName => Development ? Label : "New version: " + Label;
    public bool IsNewerThan(InstalledVersion current)
    {
        var installed = new Version(current.Version.Major, current.Version.Minor, current.Version.Build);
        if (Version != installed) return Version > installed;
        if (Development) return current.BuildNumber == 0 ? PublishedAt > current.BuiltAt : BuildNumber > current.BuildNumber;
        return current.BuildNumber > 0 && PublishedAt > current.BuiltAt;
    }
    public bool IsInstalled(InstalledVersion current) =>
        Version == new Version(current.Version.Major, current.Version.Minor, current.Version.Build) && BuildNumber == current.BuildNumber;
    // Within the installed build's channel only newer releases are offered; after switching
    // channels, the selected channel's latest release replaces whatever is installed.
    public bool IsOfferedTo(InstalledVersion current, UpdateChannel channel) =>
        !IsInstalled(current) && (current.Channel != channel || IsNewerThan(current));
}

public sealed class UpdateChecker : IDisposable
{
    private readonly HttpClient client;
    public UpdateChecker() : this(new HttpClientHandler { UseProxy = true }) { }
    public UpdateChecker(HttpMessageHandler handler)
    {
        // No explicit proxy overrides: .NET honors HTTP(S)_PROXY / ALL_PROXY (including SOCKS)
        // and the Windows user proxy/PAC settings. TLS validation remains enabled.
        client = new(handler) { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Questyle-Windows/" + InstalledVersion.Current.Version.ToString(3));
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
    }

    public async Task<UpdateRelease?> CheckAsync(UpdateChannel channel, CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync("https://api.github.com/repos/CareyChi/Questyle-Windows/releases?per_page=100", cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        return SelectRelease(document.RootElement, channel);
    }

    public static UpdateRelease? SelectRelease(JsonElement releases, UpdateChannel channel)
    {
        var candidates = new List<UpdateRelease>();
        foreach (var release in releases.EnumerateArray())
        {
            if (release.GetProperty("draft").GetBoolean()) continue;
            bool development = release.GetProperty("prerelease").GetBoolean();
            string tag = release.GetProperty("tag_name").GetString()!;
            Version? version;
            long build = 0;
            if (development)
            {
                if (channel == UpdateChannel.Stable) continue;
                var match = Regex.Match(tag, @"^dev-build-([1-9][0-9]*)$");
                if (!match.Success || !long.TryParse(match.Groups[1].Value, out build)) continue;
                // The CI writes the base application version into the release body.
                var baseVersion = Regex.Match(release.GetProperty("body").GetString() ?? "", @"(?m)^App-Version: ([0-9]+\.[0-9]+\.[0-9]+)\r?$");
                if (!baseVersion.Success || !Version.TryParse(baseVersion.Groups[1].Value, out version)) continue;
            }
            else
            {
                var match = Regex.Match(tag, @"^v([0-9]+\.[0-9]+\.[0-9]+)$");
                if (!match.Success || !Version.TryParse(match.Groups[1].Value, out version)) continue;
            }
            var package = release.GetProperty("assets").EnumerateArray().FirstOrDefault(asset => asset.GetProperty("name").GetString() == "questyle-windows-x64.zip" && asset.GetProperty("state").GetString() == "uploaded");
            if (package.ValueKind == JsonValueKind.Undefined) continue;
            // The release workflow publishes only after GitHub reports the uploaded SHA-256 digest.
            var digest = Regex.Match(package.TryGetProperty("digest", out var value) ? value.GetString() ?? "" : "", "^sha256:([0-9a-f]{64})$");
            if (!digest.Success) continue;
            candidates.Add(new(version, build, release.GetProperty("published_at").GetDateTimeOffset(),
                new Uri(package.GetProperty("url").GetString()!), package.GetProperty("size").GetInt64(), digest.Groups[1].Value));
        }
        return channel == UpdateChannel.Stable
            ? candidates.OrderByDescending(release => release.Version).ThenByDescending(release => release.PublishedAt).FirstOrDefault()
            : candidates.OrderByDescending(release => release.PublishedAt).FirstOrDefault();
    }

    public async Task DownloadAsync(UpdateRelease release, string path, Action<long> progress, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, release.Package);
        request.Headers.Accept.ParseAdd("application/octet-stream");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var target = File.Create(path);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var buffer = new byte[81920];
        long received = 0;
        try
        {
            while (true)
            {
                // Client.Timeout ends at the response headers; a stalled body is bounded here.
                stall.CancelAfter(TimeSpan.FromSeconds(30));
                int read = await source.ReadAsync(buffer, stall.Token).ConfigureAwait(false);
                if (read == 0) break;
                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                hash.AppendData(buffer, 0, read);
                received += read; progress(received);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { throw new TimeoutException("更新下载超时"); }
        if (received != release.PackageSize || Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant() != release.PackageSha256)
            throw new InvalidDataException("更新包校验失败");
    }
    public void Dispose() => client.Dispose();
}
