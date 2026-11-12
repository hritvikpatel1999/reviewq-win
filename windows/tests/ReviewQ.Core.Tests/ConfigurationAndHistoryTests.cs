using System.Text.Json;
using ReviewQ.Core;
using Xunit;

namespace ReviewQ.Core.Tests;

public sealed class ConfigurationAndHistoryTests
{
    [Theory]
    [InlineData("https://dev.azure.com/example", "example", null)]
    [InlineData("https://dev.azure.com/example/My%20Project", "example", "My Project")]
    [InlineData("https://example.visualstudio.com/DefaultCollection/Project", "example", "Project")]
    [InlineData("https://example.visualstudio.com/", "example", null)]
    public void ParsesAzureSources(string input, string org, string? project)
    {
        var source = ReviewSource.Parse(input);
        Assert.Equal(org, source.Organization); Assert.Equal(project, source.Project);
    }
    [Theory]
    [InlineData("http://dev.azure.com/example")]
    [InlineData("https://evil.example/example")]
    [InlineData("https://dev.azure.com.evil.example/example")]
    [InlineData("https://user:password@dev.azure.com/example")]
    [InlineData("https://dev.azure.com:443/example")]
    [InlineData("https://dev.azure.com/example?key=x")]
    [InlineData("https://dev.azure.com/example#fragment")]
    [InlineData("https://dev.azure.com/example/project/_git/repo")]
    [InlineData("https://dev.azure.com/example/_apis")]
    [InlineData("https://dev.azure.com/example/../project")]
    [InlineData("https://dev.azure.com/example/%2e%2e/project")]
    [InlineData("https://dev.azure.com/example/%2Fproject")]
    public void RejectsInvalidSources(string input) => Assert.Throws<ReviewException>(() => ReviewSource.Parse(input));
    [Fact]
    public void ConfigurationDefaultsAndValidation()
    {
        var s = new ReviewSettings();
        Assert.Equal(15, s.RefreshMinutes); Assert.False(s.FollowPeople); Assert.False(s.ShowAuthoredPullRequests);
        Assert.Throws<ReviewException>(() => s.Validate());
        s.Validate(false);
        s.SourceUrls = "https://dev.azure.com/a/P\nhttps://dev.azure.com/A/p";
        Assert.Throws<ReviewException>(() => s.Validate());
    }
    [Theory]
    [InlineData(1)][InlineData(5)][InlineData(15)][InlineData(30)]
    public void ValidRefreshIntervals(int minutes) => new ReviewSettings { RefreshMinutes = minutes }.Validate(false);
    [Theory]
    [InlineData(0)][InlineData(2)][InlineData(60)]
    public void InvalidRefreshIntervals(int minutes) =>
        Assert.Throws<ReviewException>(() => new ReviewSettings { RefreshMinutes = minutes }.Validate(false));
    [Fact]
    public void FollowedEmailsAreOptInAndDeduplicated()
    {
        var s = new ReviewSettings { FollowedAuthorEmails = "not valid" };
        Assert.Empty(s.FollowedEmails());
        s.FollowPeople = true; Assert.Throws<ReviewException>(() => s.Validate(false));
        s.FollowedAuthorEmails = "A@example.com;a@example.com;b@example.com";
        Assert.Equal(new[] { "a@example.com", "b@example.com" }, s.FollowedEmails());
    }
    [Theory]
    [InlineData("a")][InlineData("\U0001F469\u200d\U0001F4BB")][InlineData("e\u0301")]
    public void PromptLimitCountsGraphemes(string character)
    {
        var boundary = string.Concat(Enumerable.Repeat(character, 1000));
        Assert.Equal(1000, ReviewPrompt.CharacterCount(boundary));
        ReviewPrompt.Validate(boundary);
        Assert.Throws<ReviewException>(() => ReviewPrompt.Validate(boundary + character));
    }
    [Fact]
    public void PromptCompositionIsExactAndNoSilentTruncation()
    {
        var url = new Uri("https://dev.azure.com/example/P/_git/r/pullrequest/1");
        Assert.Equal($"Review PR: {url}", ReviewPrompt.Compose(url, " \r\n"));
        Assert.Equal($"Review PR: {url}\n\nReview\ncarefully", ReviewPrompt.Compose(url, " Review\r\ncarefully "));
        Assert.Throws<ReviewException>(() => ReviewPrompt.Compose(url, "\tunsafe"));
        Assert.Throws<ReviewException>(() => ReviewPrompt.Compose(new("https://evil.example"), ""));
    }
    private static readonly Guid Reviewer = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Other = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static Discussion Vote(int value, int? id = 1, DateTimeOffset? at = null) =>
        new(id, at, Reviewer, value, "unknown", false, true, []);
    private static Discussion Comment(string status, bool deleted = false, bool threadDeleted = false, Guid? author = null) =>
        new(1, null, null, null, status, threadDeleted, false, [new(author ?? Reviewer, deleted, true)]);
    [Theory]
    [InlineData(10, "You Approved")][InlineData(5, "You Approved with Suggestions")]
    [InlineData(-5, "You Waited for Author")][InlineData(-10, "You Rejected")]
    public void PriorVotesOverrideCommentsAndIgnoreVoteResets(int vote, string label) =>
        Assert.Equal(label, ReviewHistory.Classify([Vote(vote), Vote(0), Comment("fixed")], Reviewer).Label);
    [Fact]
    public void LatestNonzeroVoteUsesDateThenUniqueId()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.Equal("You Rejected", ReviewHistory.Classify([Vote(10, 1, now.AddMinutes(-1)), Vote(-10, 2, now)], Reviewer).Label);
        Assert.Equal("You Approved with Suggestions", ReviewHistory.Classify([Vote(10, 1, now), Vote(5, 2, now)], Reviewer).Label);
        Assert.Throws<ReviewException>(() => ReviewHistory.Classify([Vote(10), Vote(5)], Reviewer));
        Assert.Throws<ReviewException>(() => ReviewHistory.Classify([Vote(10, 1, now), Vote(5, 1, now)], Reviewer));
    }
    [Fact]
    public void DiscussionResolutionCountsThreadsNotReplies()
    {
        var fixedThread = Comment("fixed") with { Comments = [new(Reviewer, false, true), new(Reviewer, false, true)] };
        var result = ReviewHistory.Classify([fixedThread, Comment("pending"), Comment("active", author: Other)], Reviewer);
        Assert.Equal("You commented [1 resolved, 1 open]", result.Label);
        Assert.Equal(1, result.Resolved); Assert.Equal(1, result.Open);
        Assert.Equal("Your comments are resolved", ReviewHistory.Classify([fixedThread], Reviewer).Label);
    }
    [Theory]
    [InlineData("unknown")][InlineData("closed")][InlineData("wontfix")][InlineData("bydesign")]
    public void UnknownOrOtherClosureDoesNotClaimResolution(string status) =>
        Assert.Equal("You commented", ReviewHistory.Classify([Comment(status), Comment("fixed")], Reviewer).Label);
    [Fact]
    public void DeletedFeedbackDoesNotClaimResolution()
    {
        Assert.Equal("You commented", ReviewHistory.Classify([Comment("fixed", deleted: true)], Reviewer).Label);
        Assert.Equal("You commented", ReviewHistory.Classify([Comment("fixed", threadDeleted: true)], Reviewer).Label);
        Assert.Equal("First Review", ReviewHistory.Classify([Comment("fixed", author: Other)], Reviewer).Label);
    }
    [Fact]
    public void DecodesNumericStatusAndReferenceVote()
    {
        using var doc = JsonDocument.Parse("""
        [{"id":1,"status":2,"comments":[],"properties":{"CodeReviewThreadType":{"$value":"VoteUpdate"},
        "CodeReviewVoteResult":{"$value":"10"},"CodeReviewVotedByIdentity":{"$value":"1"}},
        "identities":{"1":{"id":"11111111-1111-1111-1111-111111111111"}}}]
        """);
        var decoded = ReviewHistory.Decode(doc.RootElement);
        Assert.Equal("fixed", decoded[0].Status);
        Assert.Equal("You Approved", ReviewHistory.Classify(decoded, Reviewer).Label);
    }
}
