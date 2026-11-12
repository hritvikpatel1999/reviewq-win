using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using ReviewQ.Core;

namespace ReviewQ.Windows;

public sealed class SettingsStore(string directory)
{
    public string DirectoryPath { get; } = directory;
    public string FilePath => Path.Combine(DirectoryPath, "settings.json");
    private sealed record Envelope(int Version, ReviewSettings Settings);
    public ReviewSettings Load()
    {
        if (!File.Exists(FilePath)) return new();
        try
        {
            var envelope = JsonSerializer.Deserialize<Envelope>(File.ReadAllText(FilePath));
            if (envelope is null || envelope.Version != 1 || envelope.Settings is null)
                throw new ReviewException("Settings format is unsupported. Re-enter Settings and save.");
            envelope.Settings.Validate(false);
            return envelope.Settings;
        }
        catch (JsonException) { throw new ReviewException("Saved settings are corrupt. Re-enter Settings and save."); }
    }
    public void Save(ReviewSettings settings)
    {
        settings.Validate(false);
        Directory.CreateDirectory(DirectoryPath);
        var temp = FilePath + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(new Envelope(1, settings), new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, FilePath, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}

public sealed class PrivateDiagnostics(string directory)
{
    public const long MaxLogFileBytes = 1_048_576;
    private static readonly string[] LogNames = ["reviewq.log", "reviewq.log.1", "reviewq.log.2", "reviewq.log.3"];
    private readonly object sync = new();
    public bool Verbose { get; set; }
    public string DirectoryPath { get; } = directory;
    public static readonly AsyncLocal<Guid?> Cycle = new();
    private static readonly HashSet<string> Events = ["requestStarted", "requestFinished", "refreshStarted", "refreshFinished",
        "refreshFailed", "refreshCancelled", "startup", "settingsSaved", "actionFailed", "actionFinished", "settingsFailed"];
    private static readonly HashSet<string> Operations = ["identity", "followedIdentity", "projects", "reviews",
        "reviewHistory", "reviewerPolicies", "application", "settings", "copilot", "edge", "browser", "startup", "shortcut", "diagnostics"];
    private static readonly HashSet<string> Outcomes = ["success", "httpFailure", "cancelled", "network", "invalidResponse", "system"];
    public void Write(DiagnosticRecord record)
    {
        if (!Verbose && record.Event == "requestStarted") return;
        lock (sync)
        {
            Directory.CreateDirectory(DirectoryPath);
            var path = Path.Combine(DirectoryPath, "reviewq.log");
            var line = JsonSerializer.Serialize(new
            {
                at = DateTimeOffset.UtcNow, cycle = Cycle.Value,
                @event = Events.Contains(record.Event) ? record.Event : "unknown",
                operation = Operations.Contains(record.Operation) ? record.Operation : "unknown",
                status = record.Status is >= 100 and <= 599 ? record.Status : null,
                durationMs = record.DurationMs is >= 0 ? record.DurationMs : null,
                outcome = record.Outcome is not null && Outcomes.Contains(record.Outcome) ? record.Outcome : null
            }) + Environment.NewLine;
            if (File.Exists(path) && new FileInfo(path).Length + Encoding.UTF8.GetByteCount(line) > MaxLogFileBytes)
            {
                for (var i = 3; i >= 1; i--)
                {
                    var source = i == 1 ? path : path + "." + (i - 1);
                    if (File.Exists(source)) File.Move(source, path + "." + i, true);
                }
            }
            File.AppendAllText(path, line);
        }
    }
    public long StorageBytes
    {
        get
        {
            lock (sync)
                return LogNames.Select(name => new FileInfo(Path.Combine(DirectoryPath, name)))
                    .Where(file => file.Exists).Sum(file => file.Length);
        }
    }
    public void Clear()
    {
        lock (sync)
        {
            if (!Directory.Exists(DirectoryPath)) return;
            foreach (var name in LogNames) File.Delete(Path.Combine(DirectoryPath, name));
        }
    }
    public void ShowInExplorer()
    {
        var folder = Directory.CreateDirectory(DirectoryPath);
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"))
        { UseShellExecute = true };
        start.ArgumentList.Add(folder.FullName);
        using var process = Process.Start(start) ?? throw new ReviewException("File Explorer could not be opened. Open the ReviewQ Logs folder from your local application data folder.");
    }
    public void Export(string destination)
    {
        lock (sync)
        {
            Directory.CreateDirectory(DirectoryPath);
            using var stream = new FileStream(destination, FileMode.CreateNew);
            using var zip = new ZipArchive(stream, ZipArchiveMode.Create);
            foreach (var name in LogNames)
            {
                var path = Path.Combine(DirectoryPath, name);
                if (File.Exists(path)) zip.CreateEntryFromFile(path, name);
            }
            using var writer = new StreamWriter(zip.CreateEntry("environment.txt").Open());
            writer.WriteLine($"ReviewQ {typeof(App).Assembly.GetName().Version}");
            writer.WriteLine($"OS: {Environment.OSVersion.Version}");
            writer.WriteLine($"Architecture: {RuntimeInformation.ProcessArchitecture}");
            writer.WriteLine("Only allowlisted diagnostic fields are exported. No settings, credentials, prompts, or discussion content.");
        }
    }
}

public interface IPatStore
{
    string? Load();
    void Save(string token);
    void Remove();
}

public sealed class WindowsPatStore : IPatStore
{
    private const string Target = "ReviewQ/AzureDevOps/PAT";
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public uint Flags, Type;
        public string? TargetName, Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint BlobSize;
        public IntPtr Blob;
        public uint Persist, AttributeCount;
        public IntPtr Attributes;
        public string? Alias, UserName;
    }
    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Read(string target, uint type, uint flags, out IntPtr credential);
    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Write(ref Credential credential, uint flags);
    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Delete(string target, uint type, uint flags);
    [DllImport("advapi32.dll")] private static extern void CredFree(IntPtr credential);
    public static void Validate(string token)
    {
        if (string.IsNullOrEmpty(token) || token.Length > 2048 || token.Any(char.IsWhiteSpace) || token.Any(char.IsControl))
            throw new ReviewException("Paste only a PAT value, without spaces, headers, or control characters.");
    }
    public string? Load()
    {
        if (!Read(Target, 1, 0, out var pointer))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == 1168) return null;
            throw Failure(error);
        }
        try
        {
            var credential = Marshal.PtrToStructure<Credential>(pointer);
            if (credential.BlobSize > 4096 || credential.BlobSize % 2 != 0) throw new ReviewException("The saved PAT is invalid. Replace it in Settings.");
            var token = Marshal.PtrToStringUni(credential.Blob, checked((int)credential.BlobSize / 2)) ?? "";
            Validate(token);
            return token;
        }
        finally { CredFree(pointer); }
    }
    public void Save(string token)
    {
        Validate(token);
        var bytes = Encoding.Unicode.GetBytes(token);
        var pointer = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, pointer, bytes.Length);
            var credential = new Credential { Type = 1, TargetName = Target, BlobSize = (uint)bytes.Length,
                Blob = pointer, Persist = 2, UserName = "ReviewQ" };
            if (!Write(ref credential, 0)) throw Failure(Marshal.GetLastWin32Error());
        }
        finally
        {
            Array.Clear(bytes);
            Marshal.Copy(bytes, 0, pointer, bytes.Length);
            Marshal.FreeHGlobal(pointer);
        }
    }
    public void Remove()
    {
        if (!Delete(Target, 1, 0) && Marshal.GetLastWin32Error() != 1168)
            throw Failure(Marshal.GetLastWin32Error());
    }
    private static ReviewException Failure(int error) => new($"Windows Credential Manager could not access the PAT (error {error}). Check your Windows account and retry.");
}

public static class CommandRunner
{
    public static async Task<string> RunAsync(string executable, IEnumerable<string> arguments,
        TimeSpan timeout, CancellationToken ct, IDictionary<string, string?>? environment = null,
        Func<int, string, ReviewException>? commandFailure = null)
    {
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        foreach (var key in new[] { "NODE_OPTIONS", "NODE_REPL_EXTERNAL_MODULE", "VSCODE_IPC_HOOK_CLI" })
            info.Environment.Remove(key);
        if (environment is not null)
            foreach (var pair in environment) info.Environment[pair.Key] = pair.Value;
        using var process = new Process { StartInfo = info };
        var started = false;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        try
        {
            if (!process.Start()) throw new ReviewException("The local application could not be started.");
            started = true;
            process.StandardInput.Close();
            var output = process.StandardOutput.ReadToEndAsync(deadline.Token);
            var errors = process.StandardError.ReadToEndAsync(deadline.Token);
            await process.WaitForExitAsync(deadline.Token);
            var errorText = await errors;
            var text = await output;
            if (process.ExitCode != 0)
                throw commandFailure?.Invoke(process.ExitCode, errorText) ??
                    new ReviewException($"The local command failed (exit {process.ExitCode}). Check the application/sign-in and try again.");
            return text;
        }
        catch (Win32Exception) { throw new ReviewException("The local application could not be started. Check its installation."); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new ReviewException("The local application timed out. Check it before retrying."); }
        finally
        {
            if (started && !process.HasExited)
            {
                process.Kill(true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
        }
    }
    public static IEnumerable<string> SearchPaths(string name)
    {
        return (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => Path.Combine(x.Trim('"'), name));
    }
}

public sealed class WindowsAuthorization(IPatStore patStore) : IAuthorizationProvider
{
    private string? token;
    private string tenant = "";
    private DateTimeOffset expiry;
    public void Invalidate() { token = null; tenant = ""; }
    public async Task<AuthorizationSession> GetAsync(ReviewSettings settings, CancellationToken ct)
    {
        if (settings.Authentication == AuthenticationMethod.PersonalAccessToken)
        {
            Invalidate();
            var pat = patStore.Load() ?? throw new ReviewException("No PAT is saved. Add a Code (Read) PAT in Settings.");
            WindowsPatStore.Validate(pat);
            return new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(":" + pat)), Fingerprint(pat));
        }
        var requestedTenant = settings.TenantId.Trim();
        if (token is null || tenant != requestedTenant || expiry <= DateTimeOffset.UtcNow.AddMinutes(2))
        {
            var cli = DiscoverAzureCli();
            var args = cli.Prefix.Concat(new[] { "account", "get-access-token", "--resource", "499b84ac-1321-427f-aa17-267ca6975798", "--output", "json" }).ToList();
            if (requestedTenant.Length > 0) args.AddRange(["--tenant", requestedTenant]);
            string text;
            try
            {
                text = await CommandRunner.RunAsync(cli.Executable, args, TimeSpan.FromSeconds(30), ct,
                    commandFailure: (exit, error) => AzureCliFailure(exit, error, requestedTenant));
            }
            catch (ReviewException ex) when (!ex.Message.StartsWith("Azure CLI", StringComparison.Ordinal))
            {
                throw new ReviewException("Azure CLI could not obtain an Azure DevOps access token. " + ex.Message +
                    "\nOpen PowerShell and run " + LoginCommand(requestedTenant) + ", then click Refresh in ReviewQ. " +
                    "Alternatively, choose PersonalAccessToken in Settings.");
            }
            try
            {
                using var document = JsonDocument.Parse(text);
                var root = document.RootElement;
                var accessToken = root.GetProperty("accessToken").GetString();
                if (string.IsNullOrEmpty(accessToken) || accessToken.Any(char.IsWhiteSpace) || accessToken.Any(char.IsControl))
                    throw new ReviewException("Azure CLI returned an invalid token. Update Azure CLI and sign in again.");
                if (root.TryGetProperty("expires_on", out var epoch))
                    expiry = DateTimeOffset.FromUnixTimeSeconds(long.Parse(epoch.ToString(), CultureInfo.InvariantCulture));
                else if (root.TryGetProperty("expiresOn", out var legacy) &&
                    DateTime.TryParse(legacy.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var date))
                    expiry = new DateTimeOffset(date);
                else throw new ReviewException("Azure CLI returned an invalid expiry. Update Azure CLI and sign in again.");
                if (expiry <= DateTimeOffset.UtcNow.AddSeconds(30)) throw new ReviewException("Azure CLI returned an expired token. Run az login --allow-no-subscriptions.");
                token = accessToken;
                tenant = requestedTenant;
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or FormatException or ArgumentOutOfRangeException or InvalidOperationException)
            { throw new ReviewException("Azure CLI returned an invalid response. Update it and run az login --allow-no-subscriptions."); }
        }
        return new("Bearer", token, PrincipalScope(token));
    }
    private static string LoginCommand(string tenant) => "az login --allow-no-subscriptions" +
        (tenant.Length == 0 ? "" : " --tenant <tenant-from-ReviewQ-Settings>");
    public static ReviewException AzureCliFailure(int exitCode, string error, string tenant)
    {
        bool Contains(string value) => error.Contains(value, StringComparison.OrdinalIgnoreCase);
        if (Contains("AADSTS53003") || Contains("Conditional Access") || Contains("access policy") ||
            Contains("don't have access") || Contains("does not have access"))
            return new($"Azure CLI sign-in was blocked by your organization's access policy (exit {exitCode}). " +
                "Signing in succeeded, but Microsoft Entra did not allow access to the requested resource.\n" +
                "In ReviewQ Settings, specify the Entra tenant for your Azure DevOps organization, then run " +
                LoginCommand(tenant.Length == 0 ? "configured-tenant" : tenant) +
                " in PowerShell. If access is still denied, contact your organization's IT administrator with the error code from the sign-in window. " +
                "ReviewQ cannot override this policy. Use a PAT only if your organization permits it; it does not bypass access restrictions.");
        var reason = Contains("az login") || Contains("not logged in") || Contains("no accounts")
            ? "You are not signed in, or your saved sign-in is no longer usable."
            : Contains("AADSTS") || Contains("interaction_required") || Contains("expired") || Contains("refresh token")
            ? "Microsoft Entra requires you to sign in again (for example, because of an expired session or MFA)."
            : Contains("certificate") || Contains("proxy") || Contains("connection") || Contains("timed out")
            ? "Azure CLI could not connect to Microsoft Entra. Check your connection, VPN, and corporate proxy/certificate setup."
            : Contains("No module named") || Contains("ImportError") || Contains("ModuleNotFoundError")
            ? "The Azure CLI installation could not load its required components. Repair or update Azure CLI."
            : "Azure CLI failed while requesting an Azure DevOps access token.";
        return new($"Azure CLI authentication failed (exit {exitCode}). {reason}\n" +
            $"Open PowerShell and run {LoginCommand(tenant)}, complete sign-in with the account that has Azure DevOps access, then click Refresh in ReviewQ.\n" +
            "If sign-in still fails, run az account get-access-token --resource 499b84ac-1321-427f-aa17-267ca6975798 --query expiresOn --output tsv" +
            (tenant.Length == 0 ? "" : " --tenant <tenant-from-ReviewQ-Settings>") +
            " in PowerShell to see Azure CLI's diagnostic message without printing a token. " +
            "Alternatively, choose PersonalAccessToken in Settings and save a Code (Read) PAT. Raw command output is not shown or logged to protect your account details.");
    }
    public static (string Executable, IReadOnlyList<string> Prefix) DiscoverAzureCli()
    {
        var paths = CommandRunner.SearchPaths("az.exe").Concat(CommandRunner.SearchPaths("az.cmd"))
            .Concat(new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) }
                .Where(x => x.Length > 0).Select(x => Path.Combine(x, "Microsoft SDKs", "Azure", "CLI2", "wbin", "az.cmd")));
        foreach (var path in paths.Where(File.Exists))
        {
            if (path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return (path, []);
            var directory = Path.GetDirectoryName(path)!;
            foreach (var python in new[] { Path.Combine(directory, "python.exe"), Path.GetFullPath(Path.Combine(directory, "..", "python.exe")) })
                if (File.Exists(python)) return (python, ["-IBm", "azure.cli"]);
        }
        throw new ReviewException("Azure CLI was not found in a supported Windows installation. Install Azure CLI and run az login --allow-no-subscriptions, or use a PAT.");
    }
    private static string Fingerprint(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static string PrincipalScope(string value)
    {
        var parts = value.Split('.');
        if (parts.Length == 3 && parts[1].Length <= 16384)
        {
            try
            {
                var encoded = parts[1].Replace('-', '+').Replace('_', '/');
                encoded = encoded.PadRight(encoded.Length + (4 - encoded.Length % 4) % 4, '=');
                using var document = JsonDocument.Parse(Convert.FromBase64String(encoded));
                if (document.RootElement.TryGetProperty("oid", out var oid) && Guid.TryParse(oid.GetString(), out var user) && user != Guid.Empty &&
                    document.RootElement.TryGetProperty("tid", out var tid) && Guid.TryParse(tid.GetString(), out var tenantId) && tenantId != Guid.Empty)
                    return Fingerprint($"{tenantId}/{user}");
            }
            catch (Exception ex) when (ex is FormatException or JsonException or InvalidOperationException)
            { /* An opaque token still has an isolated credential-fingerprint cache scope. */ }
        }
        return Fingerprint(value);
    }
}

public static class ReviewLauncher
{
    public static void Browser(Uri url, bool edge)
    {
        ReviewPrompt.ValidateUrl(url);
        try
        {
            Process.Start(new ProcessStartInfo(edge ? "microsoft-edge:" + url.AbsoluteUri : url.AbsoluteUri) { UseShellExecute = true })
                ?.Dispose();
        }
        catch (Win32Exception) { throw new ReviewException(edge ? "Microsoft Edge could not be opened. Install Edge or use your default browser." : "The default browser could not be opened. Check Windows default apps."); }
    }
    public static (string Executable, string CliScript) FindCode()
    {
        var candidates = new List<string>
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Microsoft VS Code", "Code.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft VS Code", "Code.exe")
        };
        candidates.AddRange(CommandRunner.SearchPaths("code.cmd").Select(x => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(x)!, "..", "Code.exe"))));
        foreach (var exe in candidates.Where(File.Exists))
        {
            if (CodeCliScript(exe) is { } script) return (exe, script);
        }
        throw new ReviewException("Install Visual Studio Code (stable) with Copilot signed in, then try again.");
    }
    public static string? CodeCliScript(string executable)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(executable))!;
        var bin = Path.Combine(directory, "bin");
        var wrapper = Path.Combine(bin, "code.cmd");
        if (File.Exists(wrapper))
        {
            // New VS Code installations keep resources in a versioned subdirectory.
            var match = Regex.Match(File.ReadAllText(wrapper), @"""%~dp0(?<relative>[^""\r\n]+cli\.js)""", RegexOptions.IgnoreCase);
            if (match.Success)
            {
                var target = Path.GetFullPath(Path.Combine(bin, match.Groups["relative"].Value));
                if (target.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && File.Exists(target))
                    return target;
            }
        }
        var legacy = Path.Combine(directory, "resources", "app", "out", "cli.js");
        return File.Exists(legacy) ? legacy : null;
    }
    public static async Task CopilotAsync(Uri url, string instructions, CancellationToken ct)
    {
        var prompt = ReviewPrompt.Compose(url, instructions);
        var code = FindCode();
        var environment = new Dictionary<string, string?> { ["ELECTRON_RUN_AS_NODE"] = "1" };
        var help = await CommandRunner.RunAsync(code.Executable, [code.CliScript, "chat", "--help"], TimeSpan.FromSeconds(15), ct, environment);
        if (!help.Contains("--mode", StringComparison.Ordinal) || !help.Contains("--new-window", StringComparison.Ordinal) || !help.Contains("[prompt]", StringComparison.Ordinal))
            throw new ReviewException("This VS Code installation does not support new agent chats. Update VS Code and retry.");
        await CommandRunner.RunAsync(code.Executable, [code.CliScript, "chat", "--mode", "agent", "--new-window", "--maximize", prompt],
            TimeSpan.FromSeconds(20), ct, environment);
    }
}

public static class StartupRegistration
{
    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true)
            ?? throw new ReviewException("Windows startup settings could not be opened.");
        if (enabled)
        {
            var executable = Environment.ProcessPath ?? throw new ReviewException("The ReviewQ executable path is unavailable.");
            key.SetValue("ReviewQ", $"\"{executable}\" --startup");
        }
        else key.DeleteValue("ReviewQ", false);
    }
}
