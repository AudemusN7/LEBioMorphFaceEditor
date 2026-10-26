using System.Net;
using System.Net.Http;
using System.Text.Json;
using MorphFaceEditor.Services;

namespace MorphFaceEditor.Tests;

public static class GitHubUpdateServiceTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("GitHub updates compare numeric versions and accept v prefixes", NumericVersions),
        new("GitHub updates keep equal and older releases current", CurrentVersions),
        new("GitHub updates reject malformed stable release responses", InvalidResponses),
        new("GitHub updates distinguish absent stable releases from request failures", MissingAndFailed),
        new("GitHub updates bound requests and preserve caller cancellation", TimeoutAndCancellation)
    ];

    private static void NumericVersions()
    {
        using var client = new HttpClient(new Handler((request, _) =>
        {
            TestAssert.Equal("https://api.github.com/repos/AudemusN7/LEBioMorphFaceEditor/releases/latest",
                request.RequestUri!.AbsoluteUri);
            TestAssert.True(request.Headers.UserAgent.Count > 0 && request.Headers.Authorization is null,
                "The public update request needs a User-Agent and no credentials.");
            return Task.FromResult(Response(Release("v0.10.2.0")));
        }));
        var result = new GitHubUpdateService(client, new Version(0, 9, 9, 9))
            .CheckForUpdatesAsync().GetAwaiter().GetResult();
        TestAssert.Equal(GitHubUpdateCheckStatus.Available, result.Status);
        TestAssert.Equal(new Version(0, 10, 2, 0), result.Release!.Version);
        TestAssert.Equal("v0.10.2.0", result.Release.TagName);
        TestAssert.Equal("https://github.com/AudemusN7/LEBioMorphFaceEditor/releases/tag/v0.10.2.0",
            result.Release.ReleaseUrl.AbsoluteUri);
        TestAssert.Equal(GitHubUpdateCheckStatus.Available, Check(Release("V1.0"), new Version(0, 10, 2, 0)).Status);
        TestAssert.Equal(GitHubUpdateCheckStatus.Available, Check(Release("0.10.2.12"), new Version(0, 10, 2, 9)).Status);
    }

    private static void CurrentVersions()
    {
        foreach (var tag in new[] { "v0.10.2.0", "0.10.2", "0.10.1.99", "0.9.99.99" })
            TestAssert.Equal(GitHubUpdateCheckStatus.Current, Check(Release(tag)).Status);
    }

    private static void InvalidResponses()
    {
        foreach (var payload in new[]
        {
            "not json", "null", "[]", "{}", "{\"tag_name\":\"v99.0.0.0\"}",
            Release("v1.0.0-beta.1"), Release("release-1.0"), Release("1.2.3.4.5"), Release("v1.0 "),
            Release("1.0.0", "https://example.com/download"),
            Release("1.0.0", "https://github.com/other/project/releases/tag/1.0.0")
        })
        {
            var result = Check(payload);
            TestAssert.Equal(GitHubUpdateCheckStatus.Failed, result.Status);
            TestAssert.Equal<GitHubUpdateCheckFailure?>(GitHubUpdateCheckFailure.InvalidResponse, result.Failure);
            TestAssert.True(result.Release is null, "A malformed response advertised a release.");
        }
    }

    private static void MissingAndFailed()
    {
        TestAssert.Equal(GitHubUpdateCheckStatus.NoRelease, Check("", status: HttpStatusCode.NotFound).Status);
        TestAssert.Equal(GitHubUpdateCheckStatus.NoRelease, Check(Release("1.0.0", prerelease: true)).Status);
        TestAssert.Equal(GitHubUpdateCheckStatus.NoRelease, Check(Release("1.0.0", draft: true)).Status);
        var forbidden = Check("", status: HttpStatusCode.Forbidden);
        TestAssert.Equal(GitHubUpdateCheckStatus.Failed, forbidden.Status);
        TestAssert.Equal<GitHubUpdateCheckFailure?>(GitHubUpdateCheckFailure.HttpError, forbidden.Failure);
        TestAssert.Equal<HttpStatusCode?>(HttpStatusCode.Forbidden, forbidden.HttpStatusCode);
        using var client = new HttpClient(new Handler((_, _) => throw new HttpRequestException("Offline")));
        var offline = new GitHubUpdateService(client, new Version(0, 10, 2, 0))
            .CheckForUpdatesAsync().GetAwaiter().GetResult();
        TestAssert.Equal(GitHubUpdateCheckStatus.Failed, offline.Status);
        TestAssert.Equal<GitHubUpdateCheckFailure?>(GitHubUpdateCheckFailure.Network, offline.Failure);
    }

    private static void TimeoutAndCancellation()
    {
        using var client = new HttpClient(new Handler(async (_, cancellation) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellation);
            throw new Exception("The pending request unexpectedly completed.");
        }));
        var timedOut = new GitHubUpdateService(client, new Version(0, 10, 2, 0), TimeSpan.FromMilliseconds(25))
            .CheckForUpdatesAsync().GetAwaiter().GetResult();
        TestAssert.Equal(GitHubUpdateCheckStatus.Failed, timedOut.Status);
        TestAssert.Equal<GitHubUpdateCheckFailure?>(GitHubUpdateCheckFailure.Timeout, timedOut.Failure);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        try
        {
            new GitHubUpdateService(client, new Version(0, 10, 2, 0))
                .CheckForUpdatesAsync(cancellation.Token).GetAwaiter().GetResult();
            throw new Exception("Caller cancellation was swallowed.");
        }
        catch (OperationCanceledException) { }
    }

    private static GitHubUpdateCheckResult Check(string payload, Version? current = null,
        HttpStatusCode status = HttpStatusCode.OK)
    {
        using var client = new HttpClient(new Handler((_, _) => Task.FromResult(Response(payload, status))));
        return new GitHubUpdateService(client, current ?? new Version(0, 10, 2, 0))
            .CheckForUpdatesAsync().GetAwaiter().GetResult();
    }

    private static string Release(string tag, string? url = null, bool prerelease = false, bool draft = false) =>
        JsonSerializer.Serialize(new
        {
            tag_name = tag,
            html_url = url ?? $"https://github.com/AudemusN7/LEBioMorphFaceEditor/releases/tag/{tag}",
            prerelease,
            draft
        });

    private static HttpResponseMessage Response(string payload, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(payload) };

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            send(request, cancellationToken);
    }
}
