using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace MorphFaceEditor.Services;

public enum GitHubUpdateCheckStatus
{
    Available,
    Current,
    NoRelease,
    Failed
}

public enum GitHubUpdateCheckFailure
{
    Network,
    Timeout,
    InvalidResponse,
    HttpError
}

public sealed record GitHubReleaseInfo(Version Version, string TagName, Uri ReleaseUrl);

public sealed record GitHubUpdateCheckResult(
    GitHubUpdateCheckStatus Status,
    GitHubReleaseInfo? Release = null,
    GitHubUpdateCheckFailure? Failure = null,
    HttpStatusCode? HttpStatusCode = null);

/// <summary>Checks public stable releases without downloading assets or requiring credentials.</summary>
public sealed class GitHubUpdateService
{
    private static readonly Uri LatestReleaseApi = new(
        "https://api.github.com/repos/AudemusN7/LEBioMorphFaceEditor/releases/latest");
    private const string ReleasePathPrefix = "/AudemusN7/LEBioMorphFaceEditor/releases/tag/";
    private readonly HttpClient _httpClient;
    private readonly Version _currentVersion;
    private readonly TimeSpan _timeout;

    // The caller owns the client, allowing reuse across automatic and manual checks.
    public GitHubUpdateService(HttpClient httpClient, Version currentVersion, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(currentVersion);
        _httpClient = httpClient;
        _currentVersion = Normalize(currentVersion);
        _timeout = timeout ?? TimeSpan.FromSeconds(10);
        if (_timeout <= TimeSpan.Zero || _timeout.TotalMilliseconds > uint.MaxValue - 1)
            throw new ArgumentOutOfRangeException(nameof(timeout));
    }

    public async Task<GitHubUpdateCheckResult> CheckForUpdatesAsync(CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);
        using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseApi);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("LEBioMorphFaceEditor", _currentVersion.ToString()));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.Add("X-GitHub-Api-Version", "2026-03-10");

        try
        {
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                timeout.Token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return new(GitHubUpdateCheckStatus.NoRelease);
            if (!response.IsSuccessStatusCode)
                return new(GitHubUpdateCheckStatus.Failed, Failure: GitHubUpdateCheckFailure.HttpError,
                    HttpStatusCode: response.StatusCode);

            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: timeout.Token).ConfigureAwait(false);
            return ReadRelease(document.RootElement);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(GitHubUpdateCheckStatus.Failed, Failure: GitHubUpdateCheckFailure.Timeout);
        }
        catch (HttpRequestException)
        {
            return new(GitHubUpdateCheckStatus.Failed, Failure: GitHubUpdateCheckFailure.Network);
        }
        catch (IOException)
        {
            return new(GitHubUpdateCheckStatus.Failed, Failure: GitHubUpdateCheckFailure.Network);
        }
        catch (JsonException)
        {
            return InvalidResponse();
        }
    }

    private GitHubUpdateCheckResult ReadRelease(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !TryReadBoolean(root, "draft", out var draft) ||
            !TryReadBoolean(root, "prerelease", out var prerelease))
            return InvalidResponse();

        // releases/latest already excludes these, but never advertise an unexpected preview release.
        if (draft || prerelease)
            return new(GitHubUpdateCheckStatus.NoRelease);

        if (!root.TryGetProperty("tag_name", out var tag) || tag.ValueKind != JsonValueKind.String ||
            !TryParseStableVersion(tag.GetString(), out var version) ||
            !root.TryGetProperty("html_url", out var url) || url.ValueKind != JsonValueKind.String ||
            !Uri.TryCreate(url.GetString(), UriKind.Absolute, out var releaseUrl) ||
            releaseUrl.Scheme != Uri.UriSchemeHttps ||
            !releaseUrl.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
            !releaseUrl.IsDefaultPort || releaseUrl.UserInfo.Length != 0 ||
            !releaseUrl.AbsolutePath.StartsWith(ReleasePathPrefix, StringComparison.OrdinalIgnoreCase) ||
            releaseUrl.AbsolutePath.Length <= ReleasePathPrefix.Length)
            return InvalidResponse();

        var release = new GitHubReleaseInfo(version!, tag.GetString()!, releaseUrl);
        return new(version! > _currentVersion ? GitHubUpdateCheckStatus.Available : GitHubUpdateCheckStatus.Current,
            release);
    }

    private static bool TryReadBoolean(JsonElement root, string property, out bool value)
    {
        value = false;
        if (!root.TryGetProperty(property, out var element) ||
            element.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            return false;
        value = element.GetBoolean();
        return true;
    }

    private static bool TryParseStableVersion(string? tag, out Version? version)
    {
        version = null;
        if (string.IsNullOrEmpty(tag)) return false;
        var numeric = tag[0] is 'v' or 'V' ? tag[1..] : tag;
        // Version.TryParse accepts signs and whitespace; release tags must be plain numeric components.
        if (numeric.Any(character => character != '.' && character is not (>= '0' and <= '9')) ||
            !Version.TryParse(numeric, out var parsed))
            return false;
        version = Normalize(parsed);
        return true;
    }

    private static Version Normalize(Version version) =>
        new(version.Major, version.Minor, Math.Max(0, version.Build), Math.Max(0, version.Revision));

    private static GitHubUpdateCheckResult InvalidResponse() =>
        new(GitHubUpdateCheckStatus.Failed, Failure: GitHubUpdateCheckFailure.InvalidResponse);
}
