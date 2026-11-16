using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;
using Microsoft.Win32.SafeHandles;
using ReviewQ.Core;

namespace ReviewQ.Windows;

public sealed record ScoutReviewState(bool CanCreateChat, bool CanEdit, string? Draft, bool CanSend,
    bool IsRunning, bool HasUserMessages);

public interface IScoutReviewControls
{
    ScoutReviewState Read();
    void NewChat();
    void SetDraft(string prompt, CancellationToken ct);
    void Send(string prompt);
}

public static class ScoutReviewAutomation
{
    public static async Task StartAsync(IScoutReviewControls controls, string prompt, CancellationToken ct,
        TimeSpan? timeout = null)
    {
        var wait = timeout ?? TimeSpan.FromSeconds(15);
        var initial = await WaitAsync(controls, state => state.CanCreateChat, wait, ct);
        RequireNoDraft(initial);
        ct.ThrowIfCancellationRequested();
        controls.NewChat();
        await WaitAsync(controls, state => state.CanEdit && IsEmptyDraft(state.Draft) &&
            !state.IsRunning && !state.HasUserMessages, wait, ct);
        ct.ThrowIfCancellationRequested();
        controls.SetDraft(prompt, ct);
        await WaitAsync(controls, state =>
        {
            if (state.Draft is { } draft && !IsEmptyDraft(draft) && draft != prompt)
                throw new ReviewException("Scout's draft changed. Nothing was submitted. Check Scout before retrying.");
            return state.CanEdit && state.Draft == prompt && state.CanSend && !state.IsRunning && !state.HasUserMessages;
        }, wait, ct);
        ct.ThrowIfCancellationRequested();
        controls.Send(prompt);
        try
        {
            await WaitAsync(controls, state => IsEmptyDraft(state.Draft) && (state.IsRunning || state.HasUserMessages),
                wait, ct);
        }
        catch (ReviewException)
        {
            throw new ReviewException("Scout's Send button was clicked, but submission could not be confirmed. Check Scout before retrying.");
        }
    }

    public static void RequireNoDraft(ScoutReviewState state)
    {
        if (state.Draft is null)
            throw new ReviewException("Scout's composer could not be read safely. Finish signing in or dismiss any dialog in Scout, then retry. Nothing was changed.");
        if (!IsEmptyDraft(state.Draft))
            throw new ReviewException("Scout has an existing draft. Send it or save it elsewhere, then clear the composer and try again. Nothing was changed.");
    }

    public static bool IsEmptyDraft(string? draft) => draft is not null && string.IsNullOrWhiteSpace(draft);

    private static async Task<ScoutReviewState> WaitAsync(IScoutReviewControls controls,
        Func<ScoutReviewState, bool> ready, TimeSpan timeout, CancellationToken ct)
    {
        var timer = Stopwatch.StartNew();
        do
        {
            ct.ThrowIfCancellationRequested();
            var state = controls.Read();
            if (ready(state)) return state;
            await Task.Delay(TimeSpan.FromMilliseconds(100), ct).ConfigureAwait(false);
        } while (timer.Elapsed < timeout);
        throw new ReviewException("Scout's chat controls are not ready. Finish signing in or dismiss any dialog in Scout, then try again.");
    }

    public static Task LaunchAsync(string executable, string prompt, CancellationToken ct) => Task.Run(async () =>
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            var start = new ProcessStartInfo(executable) { UseShellExecute = false };
            start.Environment.Remove("ELECTRON_RUN_AS_NODE");
            start.Environment.Remove("NODE_OPTIONS");
            using var launched = Process.Start(start) ?? throw new ReviewException("Microsoft Scout could not be opened.");
            var timer = Stopwatch.StartNew();
            AutomationElement? root = null;
            while (root is null && timer.Elapsed < TimeSpan.FromSeconds(15))
            {
                ct.ThrowIfCancellationRequested();
                foreach (var process in Process.GetProcessesByName("scout"))
                {
                    using (process)
                    {
                        if (process.MainWindowHandle != IntPtr.Zero &&
                            string.Equals(ExecutablePath(process), executable, StringComparison.OrdinalIgnoreCase))
                        {
                            root = AutomationElement.FromHandle(process.MainWindowHandle);
                            break;
                        }
                    }
                }
                if (root is null) await Task.Delay(100, ct).ConfigureAwait(false);
            }
            if (root is null) throw new ReviewException("Scout did not open a window. Open Scout and finish signing in, then try again.");
            if (root.TryGetCurrentPattern(WindowPattern.Pattern, out var window) &&
                ((WindowPattern)window).Current.WindowVisualState == WindowVisualState.Minimized)
                ((WindowPattern)window).SetWindowVisualState(WindowVisualState.Normal);
            await StartAsync(new WindowsScoutControls(root), prompt, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is Win32Exception or COMException or ElementNotAvailableException or
            ElementNotEnabledException or InvalidOperationException or UnauthorizedAccessException)
        {
            throw new ReviewException("Scout's chat controls could not be operated safely. Check Scout, run both apps without administrator elevation, or update Scout, then retry. Check the draft before retrying.");
        }
    }, ct);

    private static string ExecutablePath(Process process)
    {
        var path = new StringBuilder(32768);
        var length = path.Capacity;
        if (!QueryFullProcessImageName(process.SafeHandle, 0, path, ref length))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return path.ToString();
    }

    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(SafeProcessHandle process, int flags, StringBuilder path, ref int length);
}

public sealed class WindowsScoutControls(AutomationElement root) : IScoutReviewControls
{
    private AutomationElement? writtenEditor;

    private sealed record Controls(ScoutReviewState State, AutomationElement? NewChat,
        AutomationElement? Editor, AutomationElement? Send);

    public ScoutReviewState Read() => Snapshot().State;

    private Controls Snapshot()
    {
        var buttons = new List<AutomationElement>();
        var editors = new List<AutomationElement>();
        var hasMessages = false;
        var visited = 0;
        var walker = TreeWalker.ControlViewWalker;
        void Walk(AutomationElement element, int depth)
        {
            if (++visited > 8000 || depth > 40)
                throw new ReviewException("Scout's accessibility tree is too large to operate safely. Open a new chat in Scout and retry.");
            var current = element.Current;
            if (current.Name.StartsWith("You said:", StringComparison.Ordinal))
                hasMessages = true;
            if (!current.IsOffscreen)
            {
                if (current.ControlType == ControlType.Button) buttons.Add(element);
                if (current.ControlType == ControlType.Edit && current.Name == "Message") editors.Add(element);
            }
            for (var child = walker.GetFirstChild(element); child is not null; child = walker.GetNextSibling(child))
                Walk(child, depth + 1);
        }
        Walk(root, 0);
        AutomationElement? UniqueButton(string name)
        {
            var matches = buttons.Where(button => button.Current.Name == name).ToArray();
            return matches.Length == 1 ? matches[0] : null;
        }
        var newChat = UniqueButton("New chat");
        var send = UniqueButton("Send");
        var editor = editors.Count == 1 ? editors[0] : null;
        var value = editor?.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern) == true
            ? (ValuePattern)pattern : null;
        var state = new ScoutReviewState(newChat?.Current.IsEnabled == true,
            editor?.Current.IsEnabled == true && value is not null && !value.Current.IsReadOnly,
            value?.Current.Value, send?.Current.IsEnabled == true,
            buttons.Any(button => button.Current.Name == "Stop"), hasMessages);
        return new(state, newChat, editor, send);
    }

    public void NewChat()
    {
        var controls = Snapshot();
        ScoutReviewAutomation.RequireNoDraft(controls.State);
        if (!controls.State.CanCreateChat || controls.NewChat is null)
            throw new ReviewException("Scout's New chat button is unavailable. Finish signing in and retry.");
        ((InvokePattern)controls.NewChat.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
    }

    public void SetDraft(string prompt, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var controls = Snapshot();
        if (!controls.State.CanEdit || !ScoutReviewAutomation.IsEmptyDraft(controls.State.Draft) ||
            controls.State.IsRunning || controls.State.HasUserMessages || controls.Editor is null)
            throw new ReviewException("Scout is no longer in an empty new chat. Nothing was entered or submitted.");
        writtenEditor = controls.Editor;
        writtenEditor.SetFocus();
        var value = (ValuePattern)writtenEditor.GetCurrentPattern(ValuePattern.Pattern);
        if (!ScoutReviewAutomation.IsEmptyDraft(value.Current.Value))
            throw new ReviewException("Scout's draft changed. Nothing was entered or submitted.");
        var renderer = FindRenderer(writtenEditor);
        if (renderer == IntPtr.Zero)
        {
            value.SetValue(prompt);
            return;
        }

        // Scout's Chromium contenteditable advertises SetValue but ignores it.
        // Select only the verified-empty composer, including its placeholder newline.
        ((TextPattern)writtenEditor.GetCurrentPattern(TextPattern.Pattern)).DocumentRange.Select();
        var expected = "";
        var timer = Stopwatch.StartNew();
        void CheckInput()
        {
            ct.ThrowIfCancellationRequested();
            if (timer.Elapsed > TimeSpan.FromMinutes(2))
                throw new ReviewException("Entering Scout's prompt timed out. Nothing was submitted. Check the draft before retrying.");
            if (!writtenEditor.Current.HasKeyboardFocus || writtenEditor.Current.IsOffscreen ||
                writtenEditor.Current.ProcessId != root.Current.ProcessId)
                throw new ReviewException("Scout's composer lost focus. Nothing was submitted. Check the draft before retrying.");
            VerifyRenderer(renderer);
        }
        bool Matches(string actual) => expected.Length == 0
            ? ScoutReviewAutomation.IsEmptyDraft(actual)
            : actual == expected || (expected.EndsWith('\n') && actual == expected + "\n");
        void Write(string chunk)
        {
            CheckInput();
            if (!Matches(value.Current.Value))
                throw new ReviewException("Scout's draft changed. Nothing was submitted. Check Scout before retrying.");
            if (chunk == "\n") InsertLineBreak(renderer);
            else
                foreach (var character in chunk)
                {
                    CheckInput();
                    SendWindowMessage(renderer, 0x0102, character, new IntPtr(1));
                }
            expected += chunk;
            var acknowledgement = Stopwatch.StartNew();
            while (true)
            {
                CheckInput();
                if (Matches(value.Current.Value)) break;
                if (acknowledgement.Elapsed > TimeSpan.FromSeconds(2))
                    throw new ReviewException("Scout did not accept the expected prompt text. Nothing was submitted. Check the draft before retrying.");
                ct.WaitHandle.WaitOne(10);
            }
        }
        var pending = new StringBuilder();
        var elements = StringInfo.GetTextElementEnumerator(prompt);
        while (elements.MoveNext())
        {
            var element = elements.GetTextElement();
            if (element == "\n")
            {
                if (pending.Length > 0) { Write(pending.ToString()); pending.Clear(); }
                Write("\n");
            }
            else
            {
                pending.Append(element);
                if (pending.Length >= 32) { Write(pending.ToString()); pending.Clear(); }
            }
        }
        if (pending.Length > 0) Write(pending.ToString());
    }

    private IntPtr FindRenderer(AutomationElement editor)
    {
        var renderer = IntPtr.Zero;
        var ancestor = editor;
        for (var depth = 0; ancestor is not null && depth <= 40; depth++)
        {
            if (ancestor.Current.ClassName == "Chrome_RenderWidgetHostHWND")
                renderer = new IntPtr(ancestor.Current.NativeWindowHandle);
            if (Automation.Compare(ancestor, root))
            {
                if (renderer != IntPtr.Zero) VerifyRenderer(renderer);
                return renderer;
            }
            ancestor = TreeWalker.RawViewWalker.GetParent(ancestor);
        }
        throw new ReviewException("Scout's composer is no longer inside its chat window. Nothing was submitted.");
    }

    private void VerifyRenderer(IntPtr renderer)
    {
        if (GetWindowThreadProcessId(renderer, out var process) == 0 || process != root.Current.ProcessId)
            throw new ReviewException("Scout's text input window changed. Nothing was submitted.");
    }

    private static void SendWindowMessage(IntPtr window, uint message, char character, IntPtr flags)
    {
        if (SendMessageTimeout(window, message, new UIntPtr(character), flags, 2, 1000, out _) == IntPtr.Zero)
            throw new ReviewException("Scout's text input stopped responding. Nothing was submitted. Check the draft before retrying.");
    }

    private static void InsertLineBreak(IntPtr renderer)
    {
        var thread = GetWindowThreadProcessId(renderer, out _);
        var current = GetCurrentThreadId();
        var original = new byte[256];
        if (!GetKeyboardState(original) || !AttachThreadInput(current, thread, true))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            // Modifiers are scoped to Scout's attached input thread, never global input.
            var shifted = new byte[256];
            shifted[0x10] = shifted[0xA0] = 0x80;
            if (!SetKeyboardState(shifted)) throw new Win32Exception(Marshal.GetLastWin32Error());
            SendWindowMessage(renderer, 0x0100, '\r', new IntPtr(0x001C0001));
            SendWindowMessage(renderer, 0x0101, '\r', new IntPtr(unchecked((int)0xC01C0001)));
        }
        finally
        {
            var restored = SetKeyboardState(original);
            var detached = AttachThreadInput(current, thread, false);
            var callerRestored = SetKeyboardState(original);
            if (!restored || !detached || !callerRestored)
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachThreadInput(uint first, uint second, [MarshalAs(UnmanagedType.Bool)] bool attach);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetKeyboardState([Out] byte[] state);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetKeyboardState(byte[] state);

    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(IntPtr window, uint message, UIntPtr value, IntPtr flags,
        uint options, uint timeout, out UIntPtr result);

    public void Send(string prompt)
    {
        var controls = Snapshot();
        if (writtenEditor is null || controls.Editor is null ||
            !Automation.Compare(writtenEditor, controls.Editor) || controls.State.Draft != prompt ||
            !controls.State.CanEdit || !controls.State.CanSend || controls.State.IsRunning ||
            controls.State.HasUserMessages || controls.Send is null)
            throw new ReviewException("Scout's chat or draft changed. Nothing was submitted. Check Scout before retrying.");
        ((InvokePattern)controls.Send.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
    }
}
