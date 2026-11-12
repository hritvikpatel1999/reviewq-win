using System.Globalization;
using System.Net.Mail;
using System.Text.RegularExpressions;

namespace ReviewQ.Core;

public sealed class ReviewException(string message) : Exception(message);
public enum AuthenticationMethod { AzureCli, PersonalAccessToken }
public sealed record ReviewSettings
{
    public string SourceUrls { get; set; } = "";
    public AuthenticationMethod Authentication { get; set; }
    public string TenantId { get; set; } = "";
    public int RefreshMinutes { get; set; } = 15;
    public bool IncludeDrafts { get; set; }
    public bool ShowCount { get; set; } = true;
    public bool FollowPeople { get; set; }
    public string FollowedAuthorEmails { get; set; } = "";
    public bool ShowAuthoredPullRequests { get; set; }
    public string ScoutAdditionalInstructions { get; set; } = "";
    public string CopilotAdditionalInstructions { get; set; } = "";
    public bool StartAtLogin { get; set; }
    public string GlobalShortcut { get; set; } = "Ctrl+Alt+Q";
    public bool FirstCloseShown { get; set; }
    public bool HasSources => !string.IsNullOrWhiteSpace(SourceUrls);
    public IReadOnlyList<ReviewSource> Sources()
    {
        var sources = SourceUrls.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(x => ReviewSource.Parse(x.Trim())).ToList();
        if (sources.Select(x => x.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != sources.Count)
            throw new ReviewException("A source appears more than once. Remove duplicate organization/project URLs.");
        return sources;
    }
    public IReadOnlyList<string> FollowedEmails()
    {
        if (!FollowPeople) return [];
        var emails = FollowedAuthorEmails.Split(';');
        if (emails.Any(x => x.Length > 254 || x.Any(char.IsWhiteSpace)
            || !MailAddress.TryCreate(x, out var address) || address.Address != x
            || !x.Contains('.') || x.StartsWith('.') || x.Contains("..")))
            throw new ReviewException("Enter followed-author email addresses separated by semicolons, without spaces.");
        return emails.Select(x => x.ToLowerInvariant()).Distinct().ToList();
    }
    public void Validate(bool requireSources = true)
    {
        if (SourceUrls is null || TenantId is null || FollowedAuthorEmails is null ||
            ScoutAdditionalInstructions is null || CopilotAdditionalInstructions is null || GlobalShortcut is null)
            throw new ReviewException("Saved settings contain missing values. Re-enter settings.");
        if (!Enum.IsDefined(Authentication)) throw new ReviewException("Choose a supported authentication method.");
        if (requireSources && !HasSources) throw new ReviewException("Add at least one Azure DevOps organization or project URL.");
        _ = Sources();
        _ = FollowedEmails();
        ReviewPrompt.Validate(ScoutAdditionalInstructions);
        ReviewPrompt.Validate(CopilotAdditionalInstructions);
        if (RefreshMinutes is not (1 or 5 or 15 or 30)) throw new ReviewException("Refresh must be 1, 5, 15, or 30 minutes.");
        var tenant = TenantId.Trim();
        if (Authentication == AuthenticationMethod.AzureCli && tenant.Length > 0 &&
            !Guid.TryParse(tenant, out _) && !Regex.IsMatch(tenant, @"^[A-Za-z0-9][A-Za-z0-9.-]*\.[A-Za-z0-9-]+$"))
            throw new ReviewException("Enter an Entra tenant GUID or domain, or leave it blank.");
    }
}

public sealed record ReviewSource(string Organization, string? Project)
{
    public string Id => $"{Organization}/{Project}".ToLowerInvariant();
    public static ReviewSource Parse(string text)
    {
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment) || !uri.IsDefaultPort ||
            text.Any(char.IsControl) || text.Contains('\\') || Regex.IsMatch(text, @":\d+"))
            throw Invalid();
        var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(Uri.UnescapeDataString).ToList();
        string org;
        if (uri.Host.Equals("dev.azure.com", StringComparison.OrdinalIgnoreCase) && parts.Count > 0)
        { org = parts[0]; parts.RemoveAt(0); }
        else if (uri.Host.EndsWith(".visualstudio.com", StringComparison.OrdinalIgnoreCase))
        {
            org = uri.Host[..^".visualstudio.com".Length];
            if (parts.FirstOrDefault()?.Equals("DefaultCollection", StringComparison.OrdinalIgnoreCase) == true)
                parts.RemoveAt(0);
        }
        else throw Invalid();
        if (!Regex.IsMatch(org, @"^[A-Za-z0-9][A-Za-z0-9-]*$") || parts.Count > 1 ||
            parts.Any(x => string.IsNullOrWhiteSpace(x) || x.StartsWith('_') || x is "." or ".." ||
                x.Contains('/') || x.Contains('\\') || x.Any(char.IsControl)))
            throw Invalid();
        // Uri normalizes dot segments, so reject them before normalization too.
        if (text.Split('/').Select(Uri.UnescapeDataString).Any(x => x is "." or "..")) throw Invalid();
        return new(org, parts.FirstOrDefault());
    }
    private static ReviewException Invalid() =>
        new("Use https://dev.azure.com/organization or https://dev.azure.com/organization/project without query parameters.");
}

public static class ReviewPrompt
{
    public static int CharacterCount(string text) => new StringInfo(text).LengthInTextElements;
    public static void Validate(string instructions)
    {
        if (CharacterCount(instructions) > 1000)
            throw new ReviewException("Each assistant's review instructions must be 1,000 characters or fewer.");
        if (instructions.Any(c => char.IsControl(c) && c is not ('\r' or '\n' or '\u0085')))
            throw new ReviewException("Review instructions support text and line breaks, not tabs or other control characters.");
    }
    public static void ValidateUrl(Uri uri)
    {
        if (uri.Scheme != "https" || !uri.IsDefaultPort || uri.UserInfo.Length != 0 ||
            !(uri.Host.Equals("dev.azure.com", StringComparison.OrdinalIgnoreCase) ||
              uri.Host.EndsWith(".visualstudio.com", StringComparison.OrdinalIgnoreCase)) ||
            uri.AbsoluteUri.Length > 2048 || Uri.UnescapeDataString(uri.AbsoluteUri).Any(char.IsControl))
            throw new ReviewException("This pull request does not have a valid Azure DevOps HTTPS link.");
    }
    public static string Compose(Uri url, string instructions)
    {
        ValidateUrl(url);
        Validate(instructions);
        var suffix = instructions.Replace("\r\n", "\n").Replace('\r', '\n')
            .Replace('\u0085', '\n').Replace('\u2028', '\n').Replace('\u2029', '\n').Trim();
        return $"Review PR: {url.AbsoluteUri}" + (suffix.Length == 0 ? "" : "\n\n" + suffix);
    }
}
