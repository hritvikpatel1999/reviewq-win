using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ReviewQ.Core;

public sealed class AzureDevOpsClient(HttpClient http, IAuthorizationProvider authorization, string cachePath,
    Action<DiagnosticRecord>? diagnostics = null) : IReviewLoader
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private IdentityStorage cache = new(1, "", []);
    private sealed record IdentityEntry(string Key, Guid? Identity, DateTimeOffset SavedAt);
    private sealed record IdentityStorage(int Version, string Scope, List<IdentityEntry> Entries);
    private sealed class UnauthorizedResponse : Exception;
    private sealed record Page(List<JsonElement> Items, string? Continuation);

    public async Task ClearIdentitiesAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            authorization.Invalidate();
            if (File.Exists(cachePath)) File.Delete(cachePath);
            cache = new(1, "", []);
        }
        finally { gate.Release(); }
    }

    public async Task<ReviewSnapshot> LoadAsync(ReviewSettings settings, CancellationToken cancellationToken)
    {
        settings.Validate();
        await gate.WaitAsync(cancellationToken);
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                var auth = await authorization.GetAsync(settings, cancellationToken);
                try
                {
                    await ReadCacheAsync(settings, auth, cancellationToken);
                    var snapshot = await FetchAsync(settings, auth, cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    await SaveCacheAsync(cancellationToken);
                    return snapshot;
                }
                catch (UnauthorizedResponse) when (settings.Authentication == AuthenticationMethod.AzureCli && attempt == 0)
                { authorization.Invalidate(); }
                catch (UnauthorizedResponse)
                { throw new ReviewException("Authentication was rejected. Run az login --allow-no-subscriptions or replace your PAT in Settings."); }
            }
        }
        catch (JsonException) { throw Json.Invalid(); }
        catch (InvalidOperationException) { throw Json.Invalid(); }
        catch (FormatException) { throw Json.Invalid(); }
        catch (HttpRequestException) { throw new ReviewException("Cannot reach Azure DevOps. Check your connection, proxy, and organization access."); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new ReviewException("Azure DevOps timed out. Check your connection and refresh again."); }
        finally { gate.Release(); }
    }

    private async Task<ReviewSnapshot> FetchAsync(ReviewSettings settings, AuthorizationSession auth, CancellationToken ct)
    {
        var sources = settings.Sources();
        var emails = settings.FollowedEmails();
        var identities = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        var projectCache = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        var items = new Dictionary<string, ReviewItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in sources)
        {
            if (!identities.TryGetValue(source.Organization, out var user))
            {
                user = await IdentityAsync(source.Organization, null, auth, ct) ?? throw Json.Invalid();
                identities[source.Organization] = user;
            }
            IReadOnlyList<string> projects;
            if (source.Project is not null) projects = [source.Project];
            else
            {
                projects = await ProjectsAsync(source.Organization, auth, ct);
                projectCache[source.Organization] = projects;
            }
            foreach (var project in projects)
                foreach (var item in await ReviewsAsync(source.Organization, project, user, null, QueueKind.Assigned, settings, auth, items, ct))
                    items[item.Id] = item;
        }
        var matchedEmails = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var org in sources.Select(x => x.Organization).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var user = identities[org];
            if (!settings.ShowAuthoredPullRequests && emails.Count == 0) continue;
            if (!projectCache.TryGetValue(org, out var projects))
                projects = projectCache[org] = await ProjectsAsync(org, auth, ct);
            if (settings.ShowAuthoredPullRequests)
                foreach (var project in projects)
                    foreach (var item in await ReviewsAsync(org, project, user, user, QueueKind.Authored, settings, auth, items, ct))
                        items[item.Id] = item;
            var authors = new HashSet<Guid>();
            foreach (var email in emails)
                if (await IdentityAsync(org, email, auth, ct) is { } author)
                { matchedEmails.Add(email); authors.Add(author); }
            foreach (var author in authors.Where(x => !settings.ShowAuthoredPullRequests || x != user))
                foreach (var project in projects)
                    foreach (var item in await ReviewsAsync(org, project, user, author, QueueKind.Followed, settings, auth, items, ct))
                        items[item.Id] = item;
        }
        if (emails.Any(x => !matchedEmails.Contains(x)))
        {
            await SaveCacheAsync(ct);
            throw new ReviewException("A followed-author email could not be resolved. Check addresses and organization access, or disable People you follow.");
        }
        return new(items.Values.OrderByDescending(x => x.CreatedAt).ThenBy(x => x.Id, StringComparer.Ordinal).ToList());
    }

    private async Task<Guid?> IdentityAsync(string org, string? email, AuthorizationSession auth, CancellationToken ct)
    {
        var key = $"{org}/{email ?? "@current"}".ToLowerInvariant();
        var entry = cache.Entries.FirstOrDefault(x => x.Key == key);
        if (entry is not null)
        {
            var age = DateTimeOffset.UtcNow - entry.SavedAt;
            if (age >= TimeSpan.Zero && age < (entry.Identity is null ? TimeSpan.FromMinutes(15) : TimeSpan.FromDays(7)))
                return entry.Identity;
        }
        Guid? found;
        if (email is null)
        {
            var response = await GetAsync(Url(org, ["_apis", "connectionData"], [("api-version", "7.1-preview.1")]), auth, "identity", ct);
            using var doc = response.Document;
            var identity = doc.RootElement.Required("authenticatedUser");
            if (identity.TryGetProperty("isActive", out var active) && active.ValueKind == JsonValueKind.False)
                throw new UnauthorizedResponse();
            found = identity.Identity("id");
        }
        else
        {
            var page = await ListAsync(Url(org, ["_apis", "identities"],
                [("api-version", "7.1"), ("searchFilter", "General"), ("filterValue", email), ("queryMembership", "None")], true), auth, "followedIdentity", ct);
            if (page.Continuation is not null) throw Pagination();
            var matches = page.Items.Where(x => !x.Flag("isContainer") && MatchesEmail(x, email))
                .Select(x => x.Identity("id")).Distinct().ToList();
            if (matches.Count > 1) throw Json.Invalid();
            found = matches.Count == 0 ? null : matches[0];
        }
        cache.Entries.RemoveAll(x => x.Key == key);
        cache.Entries.Add(new(key, found, DateTimeOffset.UtcNow));
        if (cache.Entries.Count > 512) cache.Entries.RemoveRange(0, cache.Entries.Count - 512);
        return found;
    }

    private static bool MatchesEmail(JsonElement identity, string email)
    {
        var properties = identity.Required("properties");
        return new[] { "Mail", "Account" }.Any(key => properties.TryGetProperty(key, out var value) &&
            value.OptionalText("$value")?.Equals(email, StringComparison.OrdinalIgnoreCase) == true);
    }

    private async Task<IReadOnlyList<string>> ProjectsAsync(string org, AuthorizationSession auth, CancellationToken ct)
    {
        var projects = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var tokens = new HashSet<string>();
        string? token = null;
        do
        {
            var query = new List<(string, string)> { ("api-version", "7.1"), ("$top", "100") };
            if (token is not null) query.Add(("continuationToken", token));
            var page = await ListAsync(Url(org, ["_apis", "projects"], query), auth, "projects", ct);
            foreach (var project in page.Items) projects[project.Text("id")] = project.Text("name");
            token = page.Continuation;
            if (token is not null && !tokens.Add(token)) throw Pagination();
        } while (token is not null);
        return projects.Values.ToList();
    }

    private async Task<IReadOnlyList<ReviewItem>> ReviewsAsync(string org, string project, Guid user, Guid? creator,
        QueueKind queue, ReviewSettings settings, AuthorizationSession auth, Dictionary<string, ReviewItem> existing, CancellationToken ct)
    {
        var result = new List<ReviewItem>();
        var seen = new HashSet<int>();
        var skip = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var page = await ListAsync(Url(org, [project, "_apis", "git", "pullrequests"],
                [("api-version", "7.1"), ("searchCriteria.status", "active"),
                 (creator is null ? "searchCriteria.reviewerId" : "searchCriteria.creatorId", (creator ?? user).ToString()),
                 ("$top", "100"), ("$skip", skip.ToString(System.Globalization.CultureInfo.InvariantCulture))]), auth, "reviews", ct);
            var candidates = new List<JsonElement>();
            var newIds = 0;
            foreach (var request in page.Items)
            {
                var id = request.Integer("pullRequestId");
                if (!seen.Add(id)) continue;
                newIds++;
                if (id <= 0) throw Json.Invalid();
                if (!request.Text("status").Equals("active", StringComparison.OrdinalIgnoreCase) ||
                    (!settings.IncludeDrafts && request.Flag("isDraft"))) continue;
                var author = request.Required("createdBy").Identity("id");
                var reviewer = request.Required("reviewers").EnumerateArray().Where(x => x.Identity("id") == user).ToList();
                var repo = request.Required("repository");
                var key = $"{org}/{repo.Text("id")}/{id}";
                if (creator is not null)
                {
                    if (author != creator || (queue == QueueKind.Followed && existing.ContainsKey(key))) continue;
                }
                else if (author == user || reviewer.Count == 0 || reviewer[0].Integer("vote") != 0 || reviewer[0].Flag("hasDeclined"))
                    continue;
                candidates.Add(request);
            }
            var enriched = new ReviewItem[candidates.Count];
            await Parallel.ForEachAsync(Enumerable.Range(0, candidates.Count),
                new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct },
                async (index, token) => enriched[index] = await EnrichAsync(org, candidates[index], user, queue, auth, token));
            result.AddRange(enriched);
            if (page.Items.Count < 100) return result;
            if (newIds == 0) throw Pagination();
            skip = checked(skip + page.Items.Count);
        }
    }

    private async Task<ReviewItem> EnrichAsync(string org, JsonElement request, Guid user, QueueKind queue,
        AuthorizationSession auth, CancellationToken ct)
    {
        var repo = request.Required("repository");
        var project = repo.Required("project");
        var projectName = project.Text("name");
        var number = request.Integer("pullRequestId");
        var page = await ListAsync(Url(org, [projectName, "_apis", "git", "repositories", repo.Text("id"),
            "pullRequests", number.ToString(), "threads"], [("api-version", "7.1")]), auth, "reviewHistory", ct);
        if (page.Continuation is not null) throw Pagination();
        using var threadsDocument = JsonDocument.Parse(JsonSerializer.Serialize(page.Items));
        var threads = ReviewHistory.Decode(threadsDocument.RootElement);
        var history = queue == QueueKind.Authored ? new HistoryResult("First Review") : ReviewHistory.Classify(threads, user);
        int? active = null;
        bool ready = false;
        string explanation = queue == QueueKind.Assigned && history.Label != "First Review"
            ? "Previous review history. This PR needs your vote again." : "Recorded review history; discussion status does not prove a code change.";
        if (queue == QueueKind.Authored)
        {
            var relevant = threads.Where(x => !x.Deleted && !x.System && x.Comments.Any(c => c.IsReview && !c.Deleted)).ToList();
            if (relevant.Any(x => x.Status is not ("active" or "pending" or "fixed" or "closed" or "wontfix" or "bydesign"))) throw Json.Invalid();
            active = relevant.Count(x => x.Status is "active" or "pending");
            var approved = true;
            foreach (var reviewer in request.Required("reviewers").EnumerateArray().Where(x => x.Flag("isRequired")))
            {
                var vote = reviewer.Integer("vote");
                if (vote is not (-10 or -5 or 0 or 5 or 10)) throw Json.Invalid();
                if (reviewer.Flag("hasDeclined") || vote is not (5 or 10)) approved = false;
            }
            approved &= await PoliciesAsync(org, projectName, project.Text("id"), number, auth, ct);
            ready = approved && active == 0 && !request.Flag("isDraft");
            history = new(active > 0 ? $"{active} Active Comments" : ready ? "Merge Ready" : "Approvals pending");
            explanation = "Active Comments counts unresolved discussion threads. Merge Ready means required reviewer approvals and no active comments on a non-draft PR. Builds and other completion requirements are NOT checked.";
        }
        var requiredReviewer = request.Required("reviewers").EnumerateArray().FirstOrDefault(x => x.Identity("id") == user);
        return new()
        {
            Number = number, Title = request.Text("title"), Author = request.Required("createdBy").Text("displayName"),
            Organization = org, Project = projectName, Repository = repo.Text("name"), RepositoryId = repo.Text("id"),
            Url = Url(org, [projectName, "_git", repo.Text("id"), "pullrequest", number.ToString()], []),
            CreatedAt = request.Required("creationDate").GetDateTimeOffset(), IsDraft = request.Flag("isDraft"),
            IsRequired = requiredReviewer.ValueKind != JsonValueKind.Undefined && requiredReviewer.Flag("isRequired"),
            Queue = queue, StageLabel = history.Label, Explanation = explanation, ActiveComments = active, MergeReady = ready
        };
    }

    private async Task<bool> PoliciesAsync(string org, string project, string projectId, int number, AuthorizationSession auth, CancellationToken ct)
    {
        var artifact = $"vstfs:///CodeReview/CodeReviewId/{projectId}/{number}";
        var seen = new HashSet<Guid>();
        var approved = true;
        var skip = 0;
        while (true)
        {
            var page = await ListAsync(Url(org, [project, "_apis", "policy", "evaluations"],
                [("api-version", "7.1-preview.1"), ("artifactId", artifact), ("includeNotApplicable", "true"),
                 ("$top", "100"), ("$skip", skip.ToString())]), auth, "reviewerPolicies", ct);
            if (page.Continuation is not null) throw Pagination();
            var before = seen.Count;
            foreach (var evaluation in page.Items)
            {
                if (!Uri.UnescapeDataString(evaluation.Text("artifactId")).Equals(artifact, StringComparison.OrdinalIgnoreCase))
                    throw Json.Invalid();
                if (!seen.Add(evaluation.Identity("evaluationId"))) continue;
                var config = evaluation.Required("configuration");
                var type = config.Required("type").Identity("id").ToString();
                if (type is not ("fa4e907d-c16b-4a4c-9dfa-4906e5d171dd" or "fd2167ab-b0be-447a-8ec8-39368250530e") ||
                    !config.Required("isEnabled").GetBoolean() || !config.Required("isBlocking").GetBoolean() || config.Flag("isDeleted")) continue;
                approved &= evaluation.Text("status").ToLowerInvariant() switch
                {
                    "approved" or "notapplicable" => true,
                    "queued" or "running" or "rejected" or "broken" => false,
                    _ => throw Json.Invalid()
                };
            }
            if (page.Items.Count < 100) return approved;
            if (before == seen.Count) throw Pagination();
            skip = checked(skip + page.Items.Count);
        }
    }

    public static Uri Url(string org, IEnumerable<string> segments, IEnumerable<(string Key, string Value)> query, bool identity = false) =>
        new($"https://{(identity ? "vssps.dev.azure.com" : "dev.azure.com")}/" +
            string.Join("/", new[] { org }.Concat(segments).Select(Uri.EscapeDataString)) +
            (query.Any() ? "?" + string.Join("&", query.Select(x => $"{Uri.EscapeDataString(x.Key)}={Uri.EscapeDataString(x.Value)}")) : ""));

    private async Task<Page> ListAsync(Uri url, AuthorizationSession auth, string operation, CancellationToken ct)
    {
        var response = await GetAsync(url, auth, operation, ct);
        using var document = response.Document;
        return new(document.RootElement.Required("value").EnumerateArray().Select(x => x.Clone()).ToList(), response.Token);
    }

    private async Task<(JsonDocument Document, string? Token)> GetAsync(Uri url, AuthorizationSession auth, string operation, CancellationToken ct)
    {
        if (url.Scheme != "https" || url.Host is not ("dev.azure.com" or "vssps.dev.azure.com")) throw Json.Invalid();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (http.Timeout != Timeout.InfiniteTimeSpan) deadline.CancelAfter(http.Timeout);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue(auth.Scheme, auth.Value);
        request.Headers.Accept.Add(new("application/json"));
        var watch = Stopwatch.StartNew();
        diagnostics?.Invoke(new("requestStarted", operation));
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        diagnostics?.Invoke(new("requestFinished", operation, (int)response.StatusCode, watch.ElapsedMilliseconds,
            response.IsSuccessStatusCode ? "success" : "httpFailure"));
        if (response.StatusCode == HttpStatusCode.Unauthorized) throw new UnauthorizedResponse();
        if (!response.IsSuccessStatusCode)
            throw new ReviewException($"Azure DevOps returned HTTP {(int)response.StatusCode}. " + (response.StatusCode switch
            {
                HttpStatusCode.Forbidden => "Check account access and PAT Code (Read) permissions.",
                HttpStatusCode.NotFound => "Check the organization/project URL and account access.",
                HttpStatusCode.TooManyRequests => "Rate limit reached; wait before refreshing.",
                >= HttpStatusCode.MultipleChoices and < HttpStatusCode.BadRequest => "Redirects are blocked to protect credentials. Check sign-in.",
                _ => "Try refreshing again."
            }));
        await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
        var document = await JsonDocument.ParseAsync(stream, cancellationToken: deadline.Token);
        var token = response.Headers.TryGetValues("x-ms-continuationtoken", out var values) ? values.SingleOrDefault() : null;
        return (document, string.IsNullOrEmpty(token) ? null : token);
    }
    private static ReviewException Pagination() => new("Azure DevOps repeated or unexpectedly paginated a results page. Refresh again; the previous queue is retained.");

    private async Task ReadCacheAsync(ReviewSettings settings, AuthorizationSession auth, CancellationToken ct)
    {
        var scope = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        { auth.CacheScope, settings.Authentication, Tenant = settings.TenantId.Trim().ToLowerInvariant(), Sources = settings.Sources().Select(x => x.Id).Order().ToArray(), Emails = settings.FollowedEmails().Order().ToArray() }))));
        if (File.Exists(cachePath))
        {
            try
            {
                var stored = JsonSerializer.Deserialize<IdentityStorage>(await File.ReadAllTextAsync(cachePath, ct));
                if (stored is null || stored.Version != 1 || stored.Entries is null || stored.Entries.Count > 512 ||
                    stored.Entries.Any(x => x is null || string.IsNullOrEmpty(x.Key) || x.Identity == Guid.Empty))
                    throw new ReviewException("Identity cache is invalid. Use Re-lookup identities in Settings.");
                cache = stored;
            }
            catch (JsonException) { throw new ReviewException("Identity cache is corrupt. Use Re-lookup identities in Settings."); }
        }
        if (cache.Scope != scope) cache = new(1, scope, []);
    }
    private async Task SaveCacheAsync(CancellationToken ct)
    {
        var directory = Path.GetDirectoryName(cachePath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        var temp = cachePath + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(cache), ct);
            File.Move(temp, cachePath, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
