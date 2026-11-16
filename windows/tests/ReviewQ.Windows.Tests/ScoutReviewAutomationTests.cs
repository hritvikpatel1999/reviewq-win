using ReviewQ.Core;
using ReviewQ.Windows;
using Xunit;

namespace ReviewQ.Windows.Tests;

public sealed class ScoutReviewAutomationTests
{
    private const string Prompt = "Review PR: https://dev.azure.com/org/project/_git/repo/pullrequest/42\n\nCheck correctness.\nInclude \U0001F50D and Unicode: \u00E9.";
    private static readonly TimeSpan ShortWait = TimeSpan.FromMilliseconds(10);

    [Fact]
    public async Task StartsFreshChatAndSubmitsExactMultilinePromptOnce()
    {
        var controls = new FakeControls();
        await ScoutReviewAutomation.StartAsync(controls, Prompt, default, ShortWait);
        Assert.Equal(new[] { "new", "write", "send" }, controls.Actions);
        Assert.Equal(Prompt, controls.Written);
        Assert.Equal("", controls.State.Draft);
        Assert.True(controls.State.IsRunning);
    }

    [Fact]
    public async Task ConfirmsAnImmediatelyCompletedSubmissionFromItsUserMessage()
    {
        var controls = new FakeControls
        {
            Submitted = state => state with { Draft = "", IsRunning = false, HasUserMessages = true }
        };
        await ScoutReviewAutomation.StartAsync(controls, Prompt, default, ShortWait);
        Assert.Equal(1, controls.Actions.Count(action => action == "send"));
    }

    [Theory]
    [InlineData("Unsent draft")]
    [InlineData("\n Unsent draft \n")]
    public async Task ExistingDraftIsNotReplacedOrSubmitted(string draft)
    {
        var controls = new FakeControls { State = Empty with { Draft = draft } };
        var error = await Assert.ThrowsAsync<ReviewException>(() =>
            ScoutReviewAutomation.StartAsync(controls, Prompt, default, ShortWait));
        Assert.Contains("existing draft", error.Message);
        Assert.Empty(controls.Actions);
        Assert.Equal(draft, controls.State.Draft);
    }

    [Theory]
    [InlineData("")]
    [InlineData("\n")]
    [InlineData("\r\n")]
    [InlineData(" \t\r\n\u00A0")]
    public async Task WhitespaceOnlyComposerIsEmptyAtEveryHandoffStage(string empty)
    {
        var controls = new FakeControls
        {
            State = Empty with { Draft = empty },
            Created = state => Empty with { Draft = empty },
            Submitted = state => state with { Draft = empty, IsRunning = true }
        };
        await ScoutReviewAutomation.StartAsync(controls, Prompt, default, ShortWait);
        Assert.Equal(new[] { "new", "write", "send" }, controls.Actions);
        Assert.Equal(Prompt, controls.Written);
        Assert.Equal(empty, controls.State.Draft);
    }

    [Fact]
    public async Task UnknownComposerIsNotTreatedAsAnEmptyDraft()
    {
        var controls = new FakeControls { State = Empty with { Draft = null } };
        var error = await Assert.ThrowsAsync<ReviewException>(() =>
            ScoutReviewAutomation.StartAsync(controls, Prompt, default, ShortWait));
        Assert.Contains("could not be read", error.Message);
        Assert.Empty(controls.Actions);
    }

    [Fact]
    public async Task UnknownNewComposerIsNeverWritten()
    {
        var controls = new FakeControls { Created = state => Empty with { Draft = null } };
        await Assert.ThrowsAsync<ReviewException>(() =>
            ScoutReviewAutomation.StartAsync(controls, Prompt, default, ShortWait));
        Assert.Equal(new[] { "new" }, controls.Actions);
    }

    [Fact]
    public async Task UnknownClearedComposerDoesNotConfirmSubmission()
    {
        var controls = new FakeControls
        {
            Submitted = state => state with { Draft = null, IsRunning = true }
        };
        var error = await Assert.ThrowsAsync<ReviewException>(() =>
            ScoutReviewAutomation.StartAsync(controls, Prompt, default, ShortWait));
        Assert.Contains("submission could not be confirmed", error.Message);
        Assert.Equal(1, controls.Actions.Count(action => action == "send"));
    }

    [Fact]
    public async Task MissingOrAmbiguousNewChatControlStopsBeforeChangingAnything()
    {
        var controls = new FakeControls { State = Empty with { CanCreateChat = false } };
        var error = await Assert.ThrowsAsync<ReviewException>(() =>
            ScoutReviewAutomation.StartAsync(controls, Prompt, default, ShortWait));
        Assert.Contains("not ready", error.Message);
        Assert.Empty(controls.Actions);
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public async Task RequiresAnEditableEmptyNewConversation(bool readOnly, bool running, bool hasMessages)
    {
        var controls = new FakeControls
        {
            Created = state => state with { CanEdit = !readOnly, IsRunning = running, HasUserMessages = hasMessages }
        };
        await Assert.ThrowsAsync<ReviewException>(() =>
            ScoutReviewAutomation.StartAsync(controls, Prompt, default, ShortWait));
        Assert.Equal(new[] { "new" }, controls.Actions);
    }

    [Fact]
    public async Task ConcurrentDraftChangesAreNeverSubmitted()
    {
        var controls = new FakeControls { Entered = state => state with { Draft = "User's changed draft" } };
        var error = await Assert.ThrowsAsync<ReviewException>(() =>
            ScoutReviewAutomation.StartAsync(controls, Prompt, default, ShortWait));
        Assert.Contains("draft changed", error.Message);
        Assert.DoesNotContain("send", controls.Actions);
        Assert.Equal("User's changed draft", controls.State.Draft);
    }

    [Fact]
    public async Task NonemptyPromptIsVerifiedExactlyWithoutTrimming()
    {
        var controls = new FakeControls { Entered = state => state with { Draft = Prompt + "\n" } };
        var error = await Assert.ThrowsAsync<ReviewException>(() =>
            ScoutReviewAutomation.StartAsync(controls, Prompt, default, ShortWait));
        Assert.Contains("draft changed", error.Message);
        Assert.DoesNotContain("send", controls.Actions);
    }

    [Fact]
    public async Task CancellationDuringTextEntryNeverInvokesSend()
    {
        using var cancellation = new CancellationTokenSource();
        var controls = new FakeControls
        {
            Entered = state => { cancellation.Cancel(); return state; }
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ScoutReviewAutomation.StartAsync(controls, Prompt, cancellation.Token, ShortWait));
        Assert.Equal(new[] { "new", "write" }, controls.Actions);
    }

    [Fact]
    public async Task DisabledSendIsNotInvoked()
    {
        var controls = new FakeControls { Entered = state => state with { CanSend = false } };
        await Assert.ThrowsAsync<ReviewException>(() =>
            ScoutReviewAutomation.StartAsync(controls, Prompt, default, ShortWait));
        Assert.DoesNotContain("send", controls.Actions);
    }

    [Fact]
    public async Task UnconfirmedSubmissionIsReportedAndNeverRetried()
    {
        var controls = new FakeControls { Submitted = state => state };
        var error = await Assert.ThrowsAsync<ReviewException>(() =>
            ScoutReviewAutomation.StartAsync(controls, Prompt, default, ShortWait));
        Assert.Contains("submission could not be confirmed", error.Message);
        Assert.Equal(1, controls.Actions.Count(action => action == "send"));
    }

    [Fact]
    public async Task CancellationBeforeLaunchDoesNotTouchScout()
    {
        var controls = new FakeControls();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ScoutReviewAutomation.StartAsync(controls, Prompt, cancellation.Token, ShortWait));
        Assert.Empty(controls.Actions);
    }

    [Fact]
    public async Task CancellationAfterNewChatDoesNotEnterOrSubmitPrompt()
    {
        using var cancellation = new CancellationTokenSource();
        var controls = new FakeControls { Created = state => { cancellation.Cancel(); return state; } };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ScoutReviewAutomation.StartAsync(controls, Prompt, cancellation.Token, ShortWait));
        Assert.Equal(new[] { "new" }, controls.Actions);
    }

    [Fact]
    public async Task ScoutValidatesUrlAndInstructionsBeforeAccessingTheInstalledApp()
    {
        await Assert.ThrowsAsync<ReviewException>(() => ReviewLauncher.ScoutAsync(new Uri("https://example.com/pr/42"), "", default));
        await Assert.ThrowsAsync<ReviewException>(() => ReviewLauncher.ScoutAsync(
            new Uri("https://dev.azure.com/org/project/_git/repo/pullrequest/42"), "Invalid\tinstructions", default));
    }

    private static readonly ScoutReviewState Empty = new(true, true, "", true, false, false);

    private sealed class FakeControls : IScoutReviewControls
    {
        public ScoutReviewState State { get; set; } = Empty;
        public List<string> Actions { get; } = [];
        public string? Written { get; private set; }
        public Func<ScoutReviewState, ScoutReviewState> Created { get; init; } = state => Empty;
        public Func<ScoutReviewState, ScoutReviewState> Entered { get; init; } = state => state;
        public Func<ScoutReviewState, ScoutReviewState> Submitted { get; init; } =
            state => state with { Draft = "", IsRunning = true };
        public ScoutReviewState Read() => State;
        public void NewChat() { Actions.Add("new"); State = Created(State); }
        public void SetDraft(string prompt, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Actions.Add("write");
            Written = prompt;
            State = Entered(State with { Draft = prompt });
        }
        public void Send(string prompt)
        {
            Assert.Equal(Prompt, prompt);
            Assert.Equal(prompt, State.Draft);
            Actions.Add("send");
            State = Submitted(State);
        }
    }
}
