using System.Net;
using System.Text;
using System.Text.Json;
using ReviewQ.Core;
using Xunit;

namespace ReviewQ.Core.Tests;

public sealed class UpdateTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "ReviewQ-UpdateTests-" + Guid.NewGuid().ToString("N"));
    private DateTimeOffset now = new(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);
    private string Cache => Path.Combine(directory, "app-updates.json");
    private readonly List<DiagnosticRecord> logs = [];
    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    private UpdateStore Store(FakeClient client, string installed = "3.2.0") => new(Cache, installed, client, logs.Add, () => now);
    private static AvailableUpdate Release(string version = "3.3.0") => new(version, new Uri(AvailableUpdate.ReleaseBase + "v" + version));
    private static HttpResponseMessage Response(string version = "v3.3.0", string? url = null, bool draft = false, bool prerelease = false) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                tag_name = version, html_url = url ?? AvailableUpdate.ReleaseBase + version, draft, prerelease
            }), Encoding.UTF8, "application/json")
        };
    private Task<UpdateCheckResult> Check(Func<HttpRequestMessage, HttpResponseMessage> respond, string? etag = null, CancellationToken ct = default) =>
        CheckCore(respond, etag, ct);
    private async Task<UpdateCheckResult> CheckCore(Func<HttpRequestMessage, HttpResponseMessage> respond, string? etag, CancellationToken ct)
    {
        using var http = new HttpClient(new Handler(respond));
        return await new GitHubUpdateClient(http, () => now).CheckAsync(etag, ct);
    }

    [Theory]
    [InlineData("v3.10.0", "3.9.9", 1)]
    [InlineData("3.2.0", "v3.2.0", 0)]
    [InlineData("3.2.9", "4.0.0", -1)]
    [InlineData("3.2.10", "3.2.9", 1)]
    public void StableVersionsCompareNumerically(string left, string right, int expected) =>
        Assert.Equal(expected, Math.Sign(ReleaseVersion.Parse(left).CompareTo(ReleaseVersion.Parse(right))));

    [Theory]
    [InlineData("")]
    [InlineData("3.2")]
    [InlineData("3.2.0.1")]
    [InlineData("3.02.0")]
    [InlineData("3.2.0-beta")]
    [InlineData("3.2.0+build")]
    [InlineData(" 3.2.0")]
    [InlineData("3.2.-1")]
    [InlineData("3.2.2147483648")]
    [InlineData("٣.2.0")]
    public void MalformedVersionsAreRejected(string text) => Assert.Throws<UpdateCheckException>(() => ReleaseVersion.Parse(text));

    [Theory]
    [InlineData("https://github.com/hritvikpatel1999/reviewq/releases/tag/v3.3.0")]
    [InlineData("https://github.com.evil.example/hritvikpatel1999/reviewq-win/releases/tag/v3.3.0")]
    [InlineData("http://github.com/hritvikpatel1999/reviewq-win/releases/tag/v3.3.0")]
    [InlineData("https://github.com/hritvikpatel1999/reviewq-win/releases/tag/v4.0.0")]
    [InlineData("https://github.com/hritvikpatel1999/reviewq-win/releases/tag/v3.3.0?redirect=1")]
    [InlineData("https://user@github.com/hritvikpatel1999/reviewq-win/releases/tag/v3.3.0")]
    [InlineData("https://github.com:443/hritvikpatel1999/reviewq-win/releases/tag/v3.3.0")]
    public async Task OnlyExactWindowsReleaseLinksAreAccepted(string url) =>
        await Assert.ThrowsAsync<UpdateCheckException>(() => Check(_ => Response(url: url)));

    [Fact]
    public async Task PublicConditionalCheckNeverSendsAzureAuthorization()
    {
        var result = await Check(request =>
        {
            Assert.Equal(GitHubUpdateClient.Endpoint, request.RequestUri!.AbsoluteUri);
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Null(request.Headers.Authorization);
            Assert.False(request.Headers.Contains("Cookie"));
            Assert.Equal("\"previous\"", request.Headers.IfNoneMatch.Single().Tag);
            Assert.NotEmpty(request.Headers.UserAgent);
            var response = Response();
            response.Headers.ETag = new("\"new\"");
            return response;
        }, "\"previous\"");
        Assert.False(result.NotModified);
        Assert.Equal(Release(), result.Release);
        Assert.Equal("\"new\"", result.EntityTag);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task DraftsAndPrereleasesAreNotOffered(bool draft, bool prerelease)
    {
        var result = await Check(_ => Response("v3.3.0-beta", draft: draft, prerelease: prerelease));
        Assert.False(result.NotModified);
        Assert.Null(result.Release);
    }

    [Fact]
    public async Task NotModifiedRequiresAnEntityTag()
    {
        Assert.True((await Check(_ => new(HttpStatusCode.NotModified), "\"tag\"")).NotModified);
        await Assert.ThrowsAsync<UpdateCheckException>(() => Check(_ => new(HttpStatusCode.NotModified)));
    }

    [Theory]
    [InlineData("bad\r\nHeader")]
    [InlineData("")]
    [InlineData("unquoted")]
    public async Task InvalidEntityTagFailsBeforeRequest(string tag) =>
        await Assert.ThrowsAsync<UpdateCheckException>(() => Check(_ => throw new InvalidOperationException("Request must not be sent"), tag));

    [Fact]
    public async Task OversizedOrMalformedResponsesAreRejected()
    {
        await Assert.ThrowsAsync<UpdateCheckException>(() => Check(_ => new(HttpStatusCode.OK)
            { Content = new StringContent(new string('x', GitHubUpdateClient.MaximumResponseBytes + 1)) }));
        await Assert.ThrowsAsync<UpdateCheckException>(() => Check(_ => new(HttpStatusCode.OK)
            { Content = new UnknownLengthContent(new byte[GitHubUpdateClient.MaximumResponseBytes + 1]) }));
        await Assert.ThrowsAnyAsync<JsonException>(() => Check(_ => new(HttpStatusCode.OK) { Content = new StringContent("{broken") }));
        await Assert.ThrowsAsync<UpdateCheckException>(() => Check(_ => new(HttpStatusCode.OK) { Content = new StringContent("{}") }));
        await Assert.ThrowsAsync<UpdateCheckException>(() => Check(_ => new(HttpStatusCode.Redirect)));
    }

    [Fact]
    public async Task RateLimitHonorsTheLaterRetryHeader()
    {
        var error = await Assert.ThrowsAsync<UpdateCheckException>(() => Check(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Forbidden);
            response.Headers.Add("X-RateLimit-Remaining", "0");
            response.Headers.Add("X-RateLimit-Reset", now.AddHours(2).ToUnixTimeSeconds().ToString());
            response.Headers.RetryAfter = new(TimeSpan.FromMinutes(10));
            return response;
        }));
        Assert.Equal(now.AddHours(2), error.RetryAt);
        var fallback = await Assert.ThrowsAsync<UpdateCheckException>(() => Check(_ => new((HttpStatusCode)429)));
        Assert.Equal(now.AddHours(1), fallback.RetryAt);
        var unavailable = await Assert.ThrowsAsync<UpdateCheckException>(() => Check(_ => new(HttpStatusCode.ServiceUnavailable)));
        Assert.Null(unavailable.RetryAt);
    }

    [Fact]
    public async Task DailyChecksPersistAndManualChecksUseCachedEtag()
    {
        var client = new FakeClient { Result = new(false, Release(), "\"tag\"") };
        var store = Store(client);
        await store.CheckAsync();
        Assert.Equal(now, store.LastCheckedAt);
        Assert.Equal(Release(), store.BannerUpdate);
        now = now.AddHours(23);
        await store.CheckAsync();
        Assert.Equal(1, client.Calls);
        var restored = Store(client);
        Assert.Equal(Release(), restored.BannerUpdate);
        await restored.CheckAsync();
        Assert.Equal(1, client.Calls);
        client.Result = new(true);
        await restored.CheckAsync(manually: true);
        Assert.Equal(2, client.Calls);
        Assert.Equal("\"tag\"", client.EntityTag);
        now = now.AddDays(1);
        await restored.CheckAsync();
        Assert.Equal(3, client.Calls);
        Assert.Equal(Release(), restored.AvailableUpdate);
        Assert.False(File.Exists(Cache + ".tmp"));
    }

    [Fact]
    public async Task DismissalSurvivesRestartButNewVersionReturns()
    {
        var client = new FakeClient { Result = new(false, Release()) };
        var store = Store(client);
        await store.CheckAsync();
        store.DismissUpdate();
        Assert.Null(store.BannerUpdate);
        Assert.NotNull(store.AvailableUpdate);
        store = Store(client);
        Assert.Null(store.BannerUpdate);
        Assert.NotNull(store.AvailableUpdate);
        client.Result = new(false, Release("3.4.0"));
        await store.CheckAsync(manually: true);
        Assert.Equal("3.4.0", store.BannerUpdate!.Version);
        Assert.Null(Store(client, "3.4.0").AvailableUpdate);
        Assert.Null(Store(client, "4.0.0").AvailableUpdate);
    }

    [Fact]
    public async Task FailuresRetainReleaseAndBackOffAcrossRestart()
    {
        var client = new FakeClient { Result = new(false, Release(), "\"tag\"") };
        var store = Store(client);
        await store.CheckAsync();
        var successAt = store.LastCheckedAt;
        client.Error = new HttpRequestException("private-network-message");
        await store.CheckAsync(manually: true);
        Assert.Equal(Release(), store.BannerUpdate);
        Assert.Equal(successAt, store.LastCheckedAt);
        Assert.DoesNotContain("private-network-message", store.Error);
        await Store(client).CheckAsync();
        Assert.Equal(2, client.Calls);
        now = now.AddMinutes(15);
        await store.CheckAsync();
        Assert.Equal(3, client.Calls);
        now = now.AddMinutes(29);
        await store.CheckAsync();
        Assert.Equal(3, client.Calls);
        now = now.AddMinutes(1);
        client.Error = null;
        client.Result = new(true);
        await store.CheckAsync();
        Assert.Null(store.Error);
        Assert.Equal(4, client.Calls);
    }

    [Fact]
    public async Task ManualChecksCannotBypassServerCooldown()
    {
        var client = new FakeClient { Error = new UpdateCheckException("Limited", now.AddHours(2)) };
        var store = Store(client);
        await store.CheckAsync();
        store = Store(client);
        await store.CheckAsync(manually: true);
        Assert.Equal(1, client.Calls);
        Assert.Contains("paused by GitHub", store.Error);
        now = now.AddHours(2);
        client.Error = null;
        await store.CheckAsync(manually: true);
        Assert.Equal(2, client.Calls);
    }

    [Fact]
    public async Task CancellationAndConcurrentRequestsDoNotChangeSavedState()
    {
        var pending = new TaskCompletionSource<UpdateCheckResult>();
        var client = new FakeClient { Pending = pending.Task };
        var store = Store(client);
        using var cancel = new CancellationTokenSource();
        var checking = store.CheckAsync(ct: cancel.Token);
        Assert.True(store.IsChecking);
        await store.CheckAsync(manually: true);
        Assert.Equal(1, client.Calls);
        cancel.Cancel();
        pending.SetResult(new(false, Release()));
        await checking;
        Assert.False(store.IsChecking);
        Assert.Null(store.Error);
        Assert.Null(store.LastCheckedAt);
        Assert.Null(store.AvailableUpdate);
        Assert.False(File.Exists(Cache));
        Assert.Contains(logs, entry => entry.Event == "updateCheckCancelled");
    }

    [Fact]
    public async Task InvalidVersionAndUnsolicited304AreVisibleErrors()
    {
        var client = new FakeClient { Result = new(true) };
        var invalid = Store(client, "development");
        await invalid.CheckAsync();
        Assert.NotNull(invalid.Error);
        Assert.Equal(0, client.Calls);
        var store = Store(client);
        await store.CheckAsync();
        Assert.NotNull(store.Error);
        Assert.Null(store.LastCheckedAt);
    }

    [Theory]
    [InlineData("{broken")]
    [InlineData("{\"Schema\":99}")]
    [InlineData("{\"FailureCount\":9}")]
    [InlineData("{\"LastCheckedAt\":\"2099-01-01T00:00:00Z\"}")]
    [InlineData("{\"EntityTag\":\"\\\"tag\\\"\"}")]
    [InlineData("{\"DismissedVersion\":\"3.2.0-beta\"}")]
    [InlineData("{\"Release\":{\"Version\":\"3.3.0\",\"ReleaseUrl\":\"https://evil.example/\"},\"LastCheckedAt\":\"2026-01-01T00:00:00Z\"}")]
    public void InvalidCacheIsClearedWithoutTrustingReleaseLinks(string json)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Cache, json);
        var store = Store(new());
        Assert.Null(store.AvailableUpdate);
        Assert.NotNull(store.Error);
        Assert.False(File.Exists(Cache));
        Assert.Contains(logs, entry => entry.Event == "updateCacheInvalidated");
    }

    [Fact]
    public async Task CacheBoundsAndPersistenceErrorsAreExplicit()
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Cache, new string('x', UpdateStore.MaximumCacheBytes + 1));
        Assert.NotNull(Store(new()).Error);
        Assert.False(File.Exists(Cache));
        Directory.CreateDirectory(Cache);
        var store = Store(new() { Result = new(false, Release()) });
        await store.CheckAsync();
        Assert.Contains("could not be saved", store.Error);
        Assert.NotNull(store.AvailableUpdate);
        Assert.False(File.Exists(Cache + ".tmp"));
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
    private sealed class UnknownLengthContent(byte[] bytes) : ByteArrayContent(bytes)
    {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
    }
    private sealed class FakeClient : IUpdateClient
    {
        public UpdateCheckResult Result { get; set; } = new(false);
        public Exception? Error { get; set; }
        public Task<UpdateCheckResult>? Pending { get; set; }
        public int Calls { get; private set; }
        public string? EntityTag { get; private set; }
        public Task<UpdateCheckResult> CheckAsync(string? entityTag, CancellationToken ct)
        {
            Calls++; EntityTag = entityTag;
            return Error is { } error ? Task.FromException<UpdateCheckResult>(error) : Pending ?? Task.FromResult(Result);
        }
    }
}
