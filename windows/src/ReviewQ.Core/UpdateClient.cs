using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ReviewQ.Core;

public readonly record struct ReleaseVersion(int Major, int Minor, int Patch) : IComparable<ReleaseVersion>
{
    public static ReleaseVersion Parse(string text)
    {
        var parts = text.StartsWith('v') ? text[1..].Split('.') : text.Split('.');
        if (parts.Length != 3 || parts.Any(x => x.Length == 0 || (x.Length > 1 && x[0] == '0') ||
                x.Any(c => c is < '0' or > '9') || !int.TryParse(x, NumberStyles.None, CultureInfo.InvariantCulture, out _)))
            throw new UpdateCheckException("This build or release has no valid stable app version.");
        return new(int.Parse(parts[0], CultureInfo.InvariantCulture), int.Parse(parts[1], CultureInfo.InvariantCulture),
            int.Parse(parts[2], CultureInfo.InvariantCulture));
    }
    public int CompareTo(ReleaseVersion other)
    {
        var major = Major.CompareTo(other.Major);
        var minor = Minor.CompareTo(other.Minor);
        return major != 0 ? major : minor != 0 ? minor : Patch.CompareTo(other.Patch);
    }
    public override string ToString() => FormattableString.Invariant($"{Major}.{Minor}.{Patch}");
}

public sealed record AvailableUpdate
{
    public const string ReleaseBase = "https://github.com/hritvikpatel1999/reviewq-win/releases/tag/";
    public string Version { get; }
    public Uri ReleaseUrl { get; }
    [JsonConstructor]
    public AvailableUpdate(string version, Uri releaseUrl)
    {
        if (version is null || releaseUrl is null) throw UpdateCheckException.InvalidResponse();
        Version = ReleaseVersion.Parse(version).ToString();
        if (releaseUrl.OriginalString != ReleaseBase + Version && releaseUrl.OriginalString != ReleaseBase + "v" + Version)
            throw UpdateCheckException.InvalidResponse();
        ReleaseUrl = releaseUrl;
    }
}

public sealed class UpdateCheckException(string message, DateTimeOffset? retryAt = null) : Exception(message)
{
    public DateTimeOffset? RetryAt { get; } = retryAt;
    public static UpdateCheckException InvalidResponse() =>
        new("GitHub returned invalid update information. Try checking for updates again later.");
}

public sealed record UpdateCheckResult(bool NotModified, AvailableUpdate? Release = null, string? EntityTag = null);
public interface IUpdateClient
{
    Task<UpdateCheckResult> CheckAsync(string? entityTag, CancellationToken ct);
}

public sealed class GitHubUpdateClient(HttpClient http, Func<DateTimeOffset>? clock = null) : IUpdateClient
{
    public const string Endpoint = "https://api.github.com/repos/hritvikpatel1999/reviewq-win/releases/latest";
    public const int MaximumResponseBytes = 512 * 1024;
    public static bool ValidEntityTag(string value) =>
        value.Length is > 0 and <= 1024 && !value.Any(char.IsControl) && EntityTagHeaderValue.TryParse(value, out _);

    public async Task<UpdateCheckResult> CheckAsync(string? entityTag, CancellationToken ct)
    {
        if (entityTag is not null && !ValidEntityTag(entityTag)) throw UpdateCheckException.InvalidResponse();
        using var request = new HttpRequestMessage(HttpMethod.Get, Endpoint);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        request.Headers.UserAgent.ParseAdd("ReviewQ-Windows-update-check");
        if (entityTag is not null) request.Headers.IfNoneMatch.ParseAdd(entityTag);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if (response.StatusCode == HttpStatusCode.NotModified)
        {
            if (entityTag is null) throw UpdateCheckException.InvalidResponse();
            return new(true);
        }
        if (response.StatusCode != HttpStatusCode.OK)
        {
            var limited = (int)response.StatusCode == 429 || (response.StatusCode == HttpStatusCode.Forbidden &&
                (Header(response, "X-RateLimit-Remaining") == "0" || Header(response, "Retry-After") is not null));
            throw new UpdateCheckException(limited ? "GitHub temporarily limited update checks. Please try again later." :
                $"Could not check for ReviewQ updates (GitHub HTTP {(int)response.StatusCode}). Try again later.",
                RetryAt(response, limited));
        }
        if (response.Content.Headers.ContentLength > MaximumResponseBytes) throw UpdateCheckException.InvalidResponse();
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        using var data = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, MaximumResponseBytes + 1 - (int)data.Length)), timeout.Token)) > 0)
        {
            data.Write(buffer, 0, read);
            if (data.Length > MaximumResponseBytes) throw UpdateCheckException.InvalidResponse();
        }
        using var document = JsonDocument.Parse(data.ToArray());
        var payload = document.RootElement;
        if (payload.ValueKind != JsonValueKind.Object ||
            !payload.TryGetProperty("draft", out var draft) || draft.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
            !payload.TryGetProperty("prerelease", out var prerelease) || prerelease.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
            !payload.TryGetProperty("tag_name", out var tag) || tag.ValueKind != JsonValueKind.String ||
            !payload.TryGetProperty("html_url", out var url) || url.ValueKind != JsonValueKind.String)
            throw UpdateCheckException.InvalidResponse();
        var etag = Header(response, "ETag");
        if (etag is not null && !ValidEntityTag(etag)) throw UpdateCheckException.InvalidResponse();
        if (draft.GetBoolean() || prerelease.GetBoolean()) return new(false, EntityTag: etag);
        if (!Uri.TryCreate(url.GetString(), UriKind.Absolute, out var releaseUrl)) throw UpdateCheckException.InvalidResponse();
        timeout.Token.ThrowIfCancellationRequested();
        return new(false, new AvailableUpdate(tag.GetString()!, releaseUrl), etag);
    }
    private static string? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? string.Join(",", values) : null;
    private DateTimeOffset? RetryAt(HttpResponseMessage response, bool limited)
    {
        var now = clock?.Invoke() ?? DateTimeOffset.UtcNow;
        DateTimeOffset? retry = response.Headers.RetryAfter?.Date;
        if (response.Headers.RetryAfter?.Delta is { } delta && delta >= TimeSpan.Zero && delta < DateTimeOffset.MaxValue - now)
            retry = now + delta;
        if (limited && long.TryParse(Header(response, "X-RateLimit-Reset"), out var seconds) &&
            seconds is >= -62135596800 and <= 253402300799)
        {
            var reset = DateTimeOffset.FromUnixTimeSeconds(seconds);
            if (retry is null || reset > retry) retry = reset;
        }
        return retry > now ? retry : limited ? now.AddHours(1) : null;
    }
}
