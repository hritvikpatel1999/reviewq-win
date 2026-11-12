using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using ReviewQ.Core;
using Xunit;

namespace ReviewQ.Core.Tests;

public sealed class ClientTests : IDisposable
{
    private const string User = "11111111-1111-1111-1111-111111111111";
    private const string Author = "22222222-2222-2222-2222-222222222222";
    private const string ProjectId = "33333333-3333-3333-3333-333333333333";
    private readonly string directory = Path.Combine(Path.GetTempPath(), "ReviewQ-CoreTests-" + Guid.NewGuid());
    private readonly StubAuthorization authorization = new();
    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    private ReviewSettings Settings => new() { SourceUrls = "https://dev.azure.com/example/Project" };
    private AzureDevOpsClient Client(Handler handler, Action<DiagnosticRecord>? log = null) =>
        new(new HttpClient(handler), authorization, Path.Combine(directory, "cache.json"), log);
    private static object PullRequest(int number = 1, int vote = 0, bool draft = false, string status = "active",
        string creator = Author, bool reviewer = true, bool declined = false, string project = "Project") => new
    {
        pullRequestId = number, title = "Fixture PR", status, creationDate = $"2026-01-{Math.Clamp(number, 1, 28):00}T00:00:00Z",
        createdBy = new { id = creator, displayName = "Fixture author" },
        repository = new { id = "repo-id", name = "Repository", project = new { id = ProjectId, name = project } },
        reviewers = reviewer ? new[] { new { id = User, vote, isRequired = true, hasDeclined = declined } } : [],
        isDraft = draft
    };
    private static HttpResponseMessage JsonResponse(object data, string? continuation = null, HttpStatusCode status = HttpStatusCode.OK)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent(JsonSerializer.Serialize(data), Encoding.UTF8, "application/json") };
        if (continuation is not null) response.Headers.Add("x-ms-continuationtoken", continuation);
        return response;
    }
    private static HttpResponseMessage List(IEnumerable<object> objects, string? token = null) => JsonResponse(new { value = objects }, token);
    private static HttpResponseMessage Default(Uri uri)
    {
        if (uri.AbsolutePath.EndsWith("connectionData")) return JsonResponse(new { authenticatedUser = new { id = User, isActive = true } });
        if (uri.AbsolutePath.EndsWith("/threads")) return List([]);
        if (uri.AbsolutePath.EndsWith("/projects")) return List([new { id = ProjectId, name = "Project" }]);
        if (uri.AbsolutePath.EndsWith("/identities")) return List([new { id = Author, properties = new { Mail = new { @value = "alex@example.com" } } }]);
        if (uri.AbsolutePath.EndsWith("/evaluations")) return List([]);
        return List([PullRequest()]);
    }
    private static Dictionary<string, string> Query(Uri uri) => uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
        .Select(x => x.Split('=', 2)).ToDictionary(x => Uri.UnescapeDataString(x[0]), x => Uri.UnescapeDataString(x[1]));
    [Fact]
    public async Task AssignedFilteringAndOptionalQueuesMakeNoExtraCalls()
    {
        using var handler = new Handler(uri => uri.AbsolutePath.EndsWith("/pullrequests") ?
            List([PullRequest(1), PullRequest(2, vote: 10), PullRequest(3, vote: 5), PullRequest(4, declined: true),
                  PullRequest(5, draft: true), PullRequest(6, status: "completed"), PullRequest(7, creator: User), PullRequest(8, reviewer: false)]) : Default(uri));
        var snapshot = await Client(handler).LoadAsync(Settings, default);
        Assert.Single(snapshot.Items); Assert.Equal(1, snapshot.PendingCount); Assert.Equal(3, handler.Requests.Count);
        Assert.All(handler.Requests, x => { Assert.Equal("GET", x.Method); Assert.Equal("dev.azure.com", x.Uri.Host); });
        Assert.DoesNotContain(handler.Requests, x => x.Uri.AbsolutePath.EndsWith("/projects") || x.Uri.Host == "vssps.dev.azure.com");
    }
    [Fact]
    public async Task IncludeDraftsAndNewestFirst()
    {
        using var handler = new Handler(uri => uri.AbsolutePath.EndsWith("/pullrequests") ? List([PullRequest(1), PullRequest(2, draft: true)]) : Default(uri));
        var snapshot = await Client(handler).LoadAsync(Settings with { IncludeDrafts = true }, default);
        Assert.Equal(new[] { 2, 1 }, snapshot.Items.Select(x => x.Number));
    }
    [Fact]
    public async Task FollowedPeopleSearchAllAccessibleProjectsAndDeduplicateAssigned()
    {
        using var handler = new Handler(uri =>
        {
            if (uri.AbsolutePath.EndsWith("/identities"))
                return JsonResponse(new { value = new[] { new { id = Author, properties = new Dictionary<string, object> { ["Mail"] = new Dictionary<string, string> { ["$value"] = "alex@example.com" } } } } });
            if (uri.AbsolutePath.EndsWith("/projects")) return List([new { id = ProjectId, name = "Project" }, new { id = "other", name = "Outside" }]);
            if (uri.AbsolutePath.EndsWith("/pullrequests"))
            {
                if (!Query(uri).ContainsKey("searchCriteria.creatorId")) return List([PullRequest(1)]);
                return uri.AbsolutePath.Contains("Outside", StringComparison.Ordinal) ?
                    List([PullRequest(3, reviewer: false, project: "Outside")]) : List([PullRequest(1), PullRequest(2, vote: 10)]);
            }
            return Default(uri);
        });
        var snapshot = await Client(handler).LoadAsync(Settings with { FollowPeople = true, FollowedAuthorEmails = "alex@example.com" }, default);
        Assert.Equal(3, snapshot.Items.Count); Assert.Equal(1, snapshot.PendingCount);
        Assert.Equal(2, snapshot.Items.Count(x => x.Queue == QueueKind.Followed));
        Assert.Single(handler.Requests, x => x.Uri.AbsolutePath.EndsWith("/1/threads"));
        Assert.Single(handler.Requests, x => x.Uri.Host == "vssps.dev.azure.com");
    }
    [Fact]
    public async Task UnresolvedFollowedPersonIsAnErrorNotEmptySuccess()
    {
        using var handler = new Handler(uri => uri.AbsolutePath.EndsWith("/identities") ? List([]) : Default(uri));
        await Assert.ThrowsAsync<ReviewException>(() => Client(handler).LoadAsync(Settings with { FollowPeople = true, FollowedAuthorEmails = "missing@example.com" }, default));
    }
    [Fact]
    public async Task UnresolvedIdentitiesAreNegativelyCachedAcrossClientInstances()
    {
        using var handler = new Handler(uri => uri.AbsolutePath.EndsWith("/identities") ? List([]) : Default(uri));
        var settings = Settings with { FollowPeople = true, FollowedAuthorEmails = "missing@example.com" };
        await Assert.ThrowsAsync<ReviewException>(() => Client(handler).LoadAsync(settings, default));
        await Assert.ThrowsAsync<ReviewException>(() => Client(handler).LoadAsync(settings, default));
        Assert.Single(handler.Requests, x => x.Uri.AbsolutePath.EndsWith("/identities"));
    }

    [Fact]
    public async Task TimeoutAlsoBoundsReadingTheResponseBody()
    {
        using var handler = new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new StalledStream()) });
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(100) };
        var client = new AzureDevOpsClient(http, authorization, Path.Combine(directory, "cache.json"));
        var error = await Assert.ThrowsAsync<ReviewException>(() => client.LoadAsync(Settings, default));
        Assert.Contains("timed out", error.Message);
        Assert.False(File.Exists(Path.Combine(directory, "cache.json")));
    }
    [Fact]
    public async Task IdentityCachePersistsIsScopedAndCanBeCleared()
    {
        using var handler = new Handler(Default);
        await Client(handler).LoadAsync(Settings, default);
        await Client(handler).LoadAsync(Settings, default);
        Assert.Single(handler.Requests, x => x.Uri.AbsolutePath.EndsWith("connectionData"));
        authorization.Scope = "account-B";
        await Client(handler).LoadAsync(Settings, default);
        Assert.Equal(2, handler.Requests.Count(x => x.Uri.AbsolutePath.EndsWith("connectionData")));
        await Client(handler).ClearIdentitiesAsync(default);
        await Client(handler).LoadAsync(Settings, default);
        Assert.Equal(3, handler.Requests.Count(x => x.Uri.AbsolutePath.EndsWith("connectionData")));
        var cache = await File.ReadAllTextAsync(Path.Combine(directory, "cache.json"));
        Assert.DoesNotContain(authorization.Secret, cache); Assert.DoesNotContain("account-B", cache);
    }
    [Fact]
    public async Task ProjectContinuationTokensAreHonored()
    {
        using var handler = new Handler(uri =>
        {
            if (uri.AbsolutePath.EndsWith("/projects"))
                return Query(uri).ContainsKey("continuationToken") ? List([new { id = "p2", name = "Second" }]) :
                    List([new { id = ProjectId, name = "Project" }], "next+token");
            if (uri.AbsolutePath.Contains("Second/")) return List([]);
            return Default(uri);
        });
        await Client(handler).LoadAsync(Settings with { SourceUrls = "https://dev.azure.com/example" }, default);
        Assert.Contains(handler.Requests, x => Query(x.Uri).GetValueOrDefault("continuationToken") == "next+token");
    }
    [Fact]
    public async Task RepeatedProjectContinuationIsRejected()
    {
        using var handler = new Handler(uri => uri.AbsolutePath.EndsWith("/projects") ?
            List([new { id = ProjectId, name = "Project" }], "repeat") : Default(uri));
        await Assert.ThrowsAsync<ReviewException>(() => Client(handler).LoadAsync(Settings with { SourceUrls = "https://dev.azure.com/example" }, default));
    }
    [Fact]
    public async Task PullRequestSkipPaginationIsComplete()
    {
        using var handler = new Handler(uri => uri.AbsolutePath.EndsWith("/pullrequests") ?
            Query(uri)["$skip"] == "0" ? List(Enumerable.Range(1, 100).Select(x => PullRequest(x, vote: 10))) : List([PullRequest(101)]) : Default(uri));
        var result = await Client(handler).LoadAsync(Settings, default);
        Assert.Equal(101, Assert.Single(result.Items).Number);
        Assert.Contains(handler.Requests, x => Query(x.Uri).GetValueOrDefault("$skip") == "100");
    }
    [Fact]
    public async Task RepeatedPullRequestPageIsRejected()
    {
        using var handler = new Handler(uri => uri.AbsolutePath.EndsWith("/pullrequests") ?
            List(Enumerable.Range(1, 100).Select(x => PullRequest(x, vote: 10))) : Default(uri));
        await Assert.ThrowsAsync<ReviewException>(() => Client(handler).LoadAsync(Settings, default));
    }
    [Fact]
    public async Task UnexpectedHistoryPaginationIsRejected()
    {
        using var handler = new Handler(uri => uri.AbsolutePath.EndsWith("/threads") ? List([], "unexpected") : Default(uri));
        await Assert.ThrowsAsync<ReviewException>(() => Client(handler).LoadAsync(Settings, default));
    }
    [Theory]
    [InlineData(AuthenticationMethod.AzureCli, 2, 1)]
    [InlineData(AuthenticationMethod.PersonalAccessToken, 1, 0)]
    public async Task UnauthorizedRetriesAreBounded(AuthenticationMethod method, int authCalls, int invalidations)
    {
        using var handler = new Handler(_ => JsonResponse(new { secret = "never log this" }, status: HttpStatusCode.Unauthorized));
        await Assert.ThrowsAsync<ReviewException>(() => Client(handler).LoadAsync(Settings with { Authentication = method }, default));
        Assert.Equal(authCalls, authorization.Calls); Assert.Equal(invalidations, authorization.Invalidations);
    }
    [Theory]
    [InlineData(302)][InlineData(403)][InlineData(404)][InlineData(429)][InlineData(500)]
    public async Task HttpFailuresAreExplicitAndDoNotLeakBodies(int code)
    {
        var records = new List<DiagnosticRecord>();
        using var handler = new Handler(_ => JsonResponse(new { secret = "SENSITIVE" }, status: (HttpStatusCode)code));
        var error = await Assert.ThrowsAsync<ReviewException>(() => Client(handler, records.Add).LoadAsync(Settings, default));
        Assert.DoesNotContain("SENSITIVE", error.Message);
        Assert.DoesNotContain("SENSITIVE", JsonSerializer.Serialize(records));
        Assert.Contains(records, x => x.Status == code);
    }
    [Fact]
    public async Task MalformedPayloadIsNotAnEmptyQueue()
    {
        using var handler = new Handler(uri => uri.AbsolutePath.EndsWith("/pullrequests") ? JsonResponse(new { count = 0 }) : Default(uri));
        await Assert.ThrowsAsync<ReviewException>(() => Client(handler).LoadAsync(Settings, default));
    }
    [Theory]
    [InlineData("approved", true)][InlineData("rejected", false)][InlineData("running", false)]
    public async Task AuthoredApprovalsAndEncodedPolicyArtifact(string status, bool ready)
    {
        using var handler = new Handler(uri =>
        {
            if (uri.AbsolutePath.EndsWith("/pullrequests"))
                return Query(uri).ContainsKey("searchCriteria.creatorId") ? List([PullRequest(9, vote: 10, creator: User)]) : List([]);
            if (uri.AbsolutePath.EndsWith("/evaluations")) return List([Policy(status)]);
            return Default(uri);
        });
        var result = await Client(handler).LoadAsync(Settings with { ShowAuthoredPullRequests = true }, default);
        var item = Assert.Single(result.Items);
        Assert.Equal(QueueKind.Authored, item.Queue); Assert.Equal(ready, item.MergeReady); Assert.Equal(0, result.PendingCount);
        Assert.Contains("Builds", item.Explanation);
    }
    private static object Policy(string status, string? artifact = null) => new
    {
        evaluationId = "44444444-4444-4444-4444-444444444444", artifactId = artifact ?? $"vstfs:///CodeReview/CodeReviewId/{ProjectId}%2f9", status,
        configuration = new { isEnabled = true, isBlocking = true, type = new { id = "fa4e907d-c16b-4a4c-9dfa-4906e5d171dd" } }
    };
    [Fact]
    public async Task PolicyForAnotherPullRequestIsRejected()
    {
        using var handler = new Handler(uri =>
        {
            if (uri.AbsolutePath.EndsWith("/pullrequests")) return Query(uri).ContainsKey("searchCriteria.creatorId") ? List([PullRequest(9, creator: User, reviewer: false)]) : List([]);
            if (uri.AbsolutePath.EndsWith("/evaluations")) return List([Policy("approved", $"vstfs:///CodeReview/CodeReviewId/{ProjectId}/99")]);
            return Default(uri);
        });
        await Assert.ThrowsAsync<ReviewException>(() => Client(handler).LoadAsync(Settings with { ShowAuthoredPullRequests = true }, default));
    }
    [Fact]
    public async Task HistoryEnrichmentIsBoundedToFour()
    {
        using var handler = new Handler(uri => uri.AbsolutePath.EndsWith("/pullrequests") ?
            List(Enumerable.Range(1, 12).Select(x => PullRequest(x))) : Default(uri)) { DelayHistory = true };
        Assert.Equal(12, (await Client(handler).LoadAsync(Settings, default)).Items.Count);
        Assert.InRange(handler.MaximumHistory, 2, 4);
    }
    [Fact]
    public async Task CancellationDoesNotPersistAResult()
    {
        using var handler = new Handler(Default);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Client(handler).LoadAsync(Settings, cts.Token));
        Assert.False(File.Exists(Path.Combine(directory, "cache.json")));
    }
    private sealed class StubAuthorization : IAuthorizationProvider
    {
        public int Calls, Invalidations;
        public string Scope = "account-A";
        public string Secret => "never-persist-a-bearer-token";
        public Task<AuthorizationSession> GetAsync(ReviewSettings settings, CancellationToken ct)
        { Calls++; return Task.FromResult(new AuthorizationSession("Bearer", Secret, Scope)); }
        public void Invalidate() => Invalidations++;
    }
    private sealed class StalledStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        { await Task.Delay(Timeout.Infinite, ct); return 0; }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
    private sealed class Handler(Func<Uri, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public ConcurrentQueue<(Uri Uri, string Method)> Requests { get; } = new();
        public bool DelayHistory;
        private int currentHistory;
        public int MaximumHistory;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var uri = request.RequestUri!;
            Requests.Enqueue((uri, request.Method.Method));
            if (DelayHistory && uri.AbsolutePath.EndsWith("/threads"))
            {
                var active = Interlocked.Increment(ref currentHistory);
                int previous;
                do { previous = MaximumHistory; } while (active > previous && Interlocked.CompareExchange(ref MaximumHistory, active, previous) != previous);
                try { await Task.Delay(20, ct); return respond(uri); }
                finally { Interlocked.Decrement(ref currentHistory); }
            }
            return respond(uri);
        }
    }
}
