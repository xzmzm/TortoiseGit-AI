#if NET472
#pragma warning disable CS8632 // The annotation for nullable reference types should only be used in code within a '#nullable' annotations context.
#endif

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;
using System.Windows.Forms;
using System.Collections.Generic;
using System.Management;
using System.ComponentModel;

#region Native Methods & TortoiseGitInjector
internal static class NativeMethods
{
    [DllImport("user32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
    internal static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);
    internal const uint WmPaste = 0x0302;
    internal const uint SciSelectAll = 2013;

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr OpenThread(ThreadAccess dwDesiredAccess, bool bInheritHandle, uint dwThreadId);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern uint SuspendThread(IntPtr hThread);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern int ResumeThread(IntPtr hThread);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseHandle(IntPtr hObject);

    [Flags]
    internal enum ThreadAccess : int { SuspendResume = 0x0002 }
}

public class TortoiseGitInjector
{
    private const string CommitTextboxClassName = "Scintilla";

    public bool TryFindMessageBox(AutomationElement commitDialog, out AutomationElement? messageBox)
    {
        var textCondition = new PropertyCondition(AutomationElement.ClassNameProperty, CommitTextboxClassName);
        messageBox = commitDialog.FindFirst(TreeScope.Descendants, textCondition);
        return messageBox != null;
    }

    public string? GetCommitMessage(AutomationElement messageBox)
    {
        try { return messageBox.Current.Name; }
        catch (ElementNotAvailableException) { return null; }
    }

    public bool SetCommitMessage(AutomationElement messageBox, string text)
    {
        int nativeHandle;
        try
        {
            nativeHandle = messageBox.Current.NativeWindowHandle;
            messageBox.SetFocus();
        }
        catch (ElementNotAvailableException)
        {
            Console.WriteLine("Error: UI element disappeared before it could be used.");
            return false;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error preparing for message injection: {ex.Message}");
            return false;
        }

        var success = false;
        var thread = new Thread(() =>
        {
            IDataObject? originalClipboardData = null;
            try
            {
                var hwnd = new IntPtr(nativeHandle);
                // These operations are now safely on an STA thread.
                NativeMethods.SendMessage(hwnd, NativeMethods.SciSelectAll, IntPtr.Zero, IntPtr.Zero);
                originalClipboardData = Clipboard.GetDataObject();
                Clipboard.SetText(text);
                NativeMethods.SendMessage(hwnd, NativeMethods.WmPaste, IntPtr.Zero, IntPtr.Zero);
                success = true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error during clipboard paste operation: {ex.Message}");
                success = false;
            }
            finally
            {
                if (originalClipboardData != null)
                {
                    try { Clipboard.SetDataObject(originalClipboardData, true, 5, 100); }
                    catch (Exception clipEx) { Console.WriteLine($"Failed to restore clipboard: {clipEx.Message}"); }
                }
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        return success;
    }
}
#endregion

#region Gemini API Client
public static class GeminiApiClient
{
    private static readonly HttpClient httpClient = new HttpClient();
    private const string ApiEndpoint = "https://generativelanguage.googleapis.com/v1beta/models/gemini-flash-lite-latest:generateContent";

    static GeminiApiClient() { httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json")); }

    public static async Task<string> GenerateCommitMessageAsync(string prompt)
    {
        string? apiKey = Environment.GetEnvironmentVariable("TORTOISEGIT_GEMINI_API_KEY");
        if (string.IsNullOrEmpty(apiKey)) return "Error: TORTOISEGIT_GEMINI_API_KEY environment variable not set.";

        var requestBody = new GeminiRequest { Contents = new[] { new Content { Parts = new[] { new Part { Text = prompt } } } } };
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Post, $"{ApiEndpoint}?key={apiKey}");
            request.Content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json");
            HttpResponseMessage response = await httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                string errorContent = await response.Content.ReadAsStringAsync();
                return $"Error: API call failed with status {response.StatusCode}. Details: {errorContent}";
            }
            string jsonResponse = await response.Content.ReadAsStringAsync();
            var geminiResponse = JsonSerializer.Deserialize<GeminiResponse>(jsonResponse);
            return geminiResponse?.Candidates?.FirstOrDefault()?.Content?.Parts?.FirstOrDefault()?.Text ?? "Error: Could not parse a valid response from the API.";
        }
        catch (Exception ex) { return $"Error: An exception occurred while calling the API. {ex.Message}"; }
    }
}

public class GeminiRequest { [JsonPropertyName("contents")] public Content[]? Contents { get; set; } }
public class GeminiResponse { [JsonPropertyName("candidates")] public Candidate[]? Candidates { get; set; } }
public class Candidate { [JsonPropertyName("content")] public Content? Content { get; set; } }
public class Content { [JsonPropertyName("parts")] public Part[]? Parts { get; set; } }
public class Part { [JsonPropertyName("text")] public string? Text { get; set; } }
#endregion

#region Git & Repo Helpers
public static class GitDiffHelper
{
    private static async Task<(string output, string error, int exitCode)> RunGitCommandAsync(string arguments, string workingDirectory)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = workingDirectory,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            }
        };

        var outputBuilder = new StringBuilder();
        var errorBuilder = new StringBuilder();

        process.OutputDataReceived += (sender, args) => { if (args.Data != null) outputBuilder.AppendLine(args.Data); };
        process.ErrorDataReceived += (sender, args) => { if (args.Data != null) errorBuilder.AppendLine(args.Data); };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

#if NET472
        process.WaitForExit();
        await Task.CompletedTask; // Since this is an async method, we need to return a Task.
#else
        await process.WaitForExitAsync();
#endif

        return (outputBuilder.ToString().TrimEnd(), errorBuilder.ToString().TrimEnd(), process.ExitCode);
    }

    public static async Task<string> GetDiffAsync(string workingDirectory)
    {
        try
        {
            // Appending ' .' scopes the commands to the current working directory.
            // This is crucial for when a commit is initiated from a subdirectory,
            // ensuring we only get the diff for files within that scope, matching TortoiseGit's behavior.
            var (statusOutput, statusError, statusExitCode) = await RunGitCommandAsync("status --short .", workingDirectory);
            if (statusExitCode != 0)
            {
                Console.WriteLine($"Warning: 'git status' command failed with exit code {statusExitCode}:\n{statusError}");
            }

            if (string.IsNullOrWhiteSpace(statusOutput))
            {
                return string.Empty;
            }

            var fullContext = new StringBuilder();

            var (logOutput, logError, logExitCode) = await RunGitCommandAsync("log -n 5 --oneline --no-decorate", workingDirectory);
            if (logExitCode == 0 && !string.IsNullOrWhiteSpace(logOutput))
            {
                fullContext.AppendLine("Recent commits:");
                fullContext.AppendLine(logOutput);
                fullContext.AppendLine();
            }
            else if (logExitCode != 0) { Console.WriteLine($"Warning: 'git log' failed with exit code {logExitCode}:\n{logError}"); }

            fullContext.AppendLine("Changed files:");
            fullContext.AppendLine(statusOutput);
            fullContext.AppendLine();

            fullContext.AppendLine("Full diff:");

            const int MaxDiffBytes = 30 * 1024; // 30KB

            var (diffOutput, diffError, diffExitCode) = await RunGitCommandAsync("diff HEAD .", workingDirectory);
            if (diffExitCode != 0) return $"Error: 'git diff HEAD' failed.\n{diffError}";

            var (cachedDiffOutput, cachedDiffError, cachedDiffExitCode) = await RunGitCommandAsync("diff --cached HEAD .", workingDirectory);
            if (cachedDiffExitCode != 0) return $"Error: 'git diff --cached HEAD' failed.\n{cachedDiffError}";

            var combinedDiffBuilder = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(diffOutput)) combinedDiffBuilder.AppendLine(diffOutput);
            if (!string.IsNullOrWhiteSpace(cachedDiffOutput)) combinedDiffBuilder.AppendLine(cachedDiffOutput);
            string combinedDiff = combinedDiffBuilder.ToString().TrimEnd();

            if (string.IsNullOrWhiteSpace(combinedDiff))
            {
                // No diff content to add
            }
            else if (Encoding.UTF8.GetByteCount(combinedDiff) > MaxDiffBytes)
            {
                Console.WriteLine($"Warning: Total diff is large. Truncating individual files that exceed {MaxDiffBytes / 1024}KB.");

                var processedDiffs = new StringBuilder();
                // Split the combined diff into diffs for individual files.
                // The pattern splits on a newline that is followed by "diff --git ".
                string[] fileDiffs = Regex.Split(combinedDiff, @"\r?\n(?=diff --git )");

                for (int i = 0; i < fileDiffs.Length; i++)
                {
                    string fileDiff = fileDiffs[i];
                    if (string.IsNullOrWhiteSpace(fileDiff)) continue;

                    // Re-add the newline separator that was consumed by the split regex.
                    if (i > 0) processedDiffs.AppendLine();

                    if (Encoding.UTF8.GetByteCount(fileDiff) > MaxDiffBytes)
                    {
                        string marker = "\n[... diff for this file truncated due to size ...]";
                        long textBudget = MaxDiffBytes - Encoding.UTF8.GetByteCount(marker);
                        if (textBudget < 0) textBudget = 0;

                        // Start with a proportional guess to make the loop faster
                        int len = (int)(fileDiff.Length * ((double)textBudget / Encoding.UTF8.GetByteCount(fileDiff)));
                        // Reduce length until it's safely under the byte limit
                        while (len > 0 && Encoding.UTF8.GetByteCount(fileDiff.Substring(0, len)) > textBudget) { len--; }

                        // Find last newline to avoid cutting a line
                        int cutIndex = fileDiff.LastIndexOf('\n', len > 0 ? len - 1 : 0);
                        if (cutIndex <= 0) cutIndex = len;

                        processedDiffs.Append(fileDiff.Substring(0, cutIndex));
                        processedDiffs.Append(marker);
                    }
                    else
                    {
                        processedDiffs.Append(fileDiff);
                    }
                }
                fullContext.AppendLine(processedDiffs.ToString());
            }
            else
            {
                fullContext.AppendLine(combinedDiff);
            }

            return fullContext.ToString();
        }
        catch (Win32Exception)
        {
            return "Error: 'git' command not found. Ensure Git is installed and in your system's PATH.";
        }
        catch (Exception ex)
        {
            return $"Error: An unexpected exception occurred while gathering git context. {ex.Message}";
        }
    }
}

public static class RepoFinder
{
    public static string? FindRepoRootFromDialog(AutomationElement commitDialog)
    {
        try
        {
            int processId = commitDialog.Current.ProcessId;
            string? startingPath = GetPathFromProcess(processId);

            if (string.IsNullOrEmpty(startingPath))
            {
                Console.WriteLine("Could not find path from command line, trying window title as fallback.");
                int index = commitDialog.Current.Name.IndexOf(" - Commit");
                if (index >= 0)
                    startingPath = commitDialog.Current.Name.Substring(0, index);
                else
                {
                    var match = Regex.Match(commitDialog.Current.Name, @"([A-Z]:\\[^-\r\n]+)");
                    if (match.Success)
                    {
                        startingPath = match.Groups[1].Value.Trim();
                    }
                }
            }

            if (string.IsNullOrEmpty(startingPath))
            {
                Console.WriteLine($"Could not determine a starting path from title: '{commitDialog.Current.Name}'");
                return null;
            }
            Console.WriteLine($"Found starting path: {startingPath}");

            // The starting path *is* the working directory TortoiseGit uses, no need to search for .git root
            return startingPath;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error finding repository root: {ex.Message}");
            return null;
        }
    }

    private static string? GetPathFromProcess(int processId)
    {
        try
        {
            string query = $"SELECT CommandLine FROM Win32_Process WHERE ProcessId = {processId}";
            using var searcher = new ManagementObjectSearcher(query);
            using var collection = searcher.Get();
            var commandLine = collection.OfType<ManagementObject>().Select(p => (string)p["CommandLine"]).FirstOrDefault();
            if (commandLine == null) return null;
            var match = Regex.Match(commandLine, @"/path:""([^""]+)""");
            return match.Success ? match.Groups[1].Value : null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"WMI query failed: {ex.Message}.");
            return null;
        }
    }
}
#endregion

#region Event-Driven Watcher
public class CommitDialogWatcher : IDisposable
{
    private readonly TortoiseGitInjector injector;
    private readonly AutomationEventHandler windowOpenedHandler;
    private readonly AutomationEventHandler windowClosedHandler;
    private readonly HashSet<int> tgitProcessIds = new HashSet<int>();
    private readonly System.Threading.Timer pollingTimer;
    private int isProcessing = 0;
    private bool disposed = false;
    private readonly HashSet<IntPtr> processedDialogs = new HashSet<IntPtr>();

    public CommitDialogWatcher(TortoiseGitInjector injector)
    {
        this.injector = injector;
        // Handler delegates are stored in fields to prevent them from being garbage collected
        this.windowOpenedHandler = this.OnWindowOpened;
        this.windowClosedHandler = this.OnWindowClosed;
        
        // Initialize fallback polling timer
        this.pollingTimer = new System.Threading.Timer(this.PollForCommitDialogs, null, Timeout.Infinite, Timeout.Infinite);
    }

    public void Start()
    {
        Console.WriteLine("Starting event-driven monitoring for TortoiseGit commit dialog...");
        this.UpdateTgitProcessList();
        
        try
        {
            Automation.AddAutomationEventHandler(
                WindowPattern.WindowOpenedEvent,
                AutomationElement.RootElement,
                TreeScope.Children,
                this.windowOpenedHandler);
            Automation.AddAutomationEventHandler(
                WindowPattern.WindowClosedEvent,
                AutomationElement.RootElement,
                TreeScope.Children,
                this.windowClosedHandler);
            Console.WriteLine("UI Automation event handler registered successfully.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to register UI Automation event handler: {ex.Message}");
            Console.WriteLine("Falling back to polling mode only.");
        }
        
        // Start fallback polling every 2 seconds as a backup
        this.pollingTimer.Change(TimeSpan.Zero, TimeSpan.FromSeconds(2));
        Console.WriteLine("Fallback polling timer started (checks every 2 seconds).");
    }

    private void UpdateTgitProcessList()
    {
        this.tgitProcessIds.Clear();
        try
        {
            foreach (var p in Process.GetProcessesByName("TortoiseGitProc"))
            {
                this.tgitProcessIds.Add(p.Id);
            }
            Console.WriteLine($"Updated TortoiseGit process list: {this.tgitProcessIds.Count} processes found.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error updating process list: {ex.Message}");
        }
    }

    private async void OnWindowOpened(object sender, AutomationEventArgs e)
    {
        if (this.disposed) return;
        
        Console.WriteLine($"Window opened event triggered: {e.EventId}");
        
        if (sender is not AutomationElement openedWindow) return;

        try
        {
            Console.WriteLine($"Window opened: {openedWindow.Current.Name} (PID: {openedWindow.Current.ProcessId})");
            
            // Refresh the process list in case TortoiseGitProc just started with this dialog
            this.UpdateTgitProcessList();

            // Quick filters to ignore irrelevant windows
            if (!this.tgitProcessIds.Contains(openedWindow.Current.ProcessId))
            {
                Console.WriteLine($"Ignoring window - not a TortoiseGit process (PID: {openedWindow.Current.ProcessId})");
                return;
            }
            
            if (openedWindow.Current.Name.IndexOf("Commit", StringComparison.OrdinalIgnoreCase) < 0)
            {
                Console.WriteLine($"Ignoring window - name doesn't contain 'Commit': {openedWindow.Current.Name}");
                return;
            }

            Console.WriteLine("\n--- Potential Commit Dialog Found by Event ---");

            if (this.injector.TryFindMessageBox(openedWindow, out var messageBox) && messageBox != null)
            {
                // We found it! Process it asynchronously to avoid blocking the UI event handler thread.
                await this.ProcessCommitDialog(openedWindow, messageBox);
            }
            else
            {
                Console.WriteLine("Window found but no message box detected.");
            }
        }
        catch (ElementNotAvailableException)
        {
            Console.WriteLine("Window opened event: Element not available (window closed quickly).");
            // The window might have closed very quickly, which is fine.
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error in event handler: {ex.Message}");
        }
    }
    
    private void OnWindowClosed(object sender, AutomationEventArgs e)
    {
        if (this.disposed) return;
        if (sender is not AutomationElement closedWindow) return;

        try
        {
            // The handle should be available right as it closes.
            var dialogHandle = new IntPtr(closedWindow.Current.NativeWindowHandle);
            if (this.processedDialogs.Remove(dialogHandle))
            {
                Console.WriteLine($"Dialog {dialogHandle} closed. Removed from processed list.");
            }
        }
        catch (ElementNotAvailableException)
        {
            // This is expected if the window is gone completely. We can't clean up this handle, but we tried.
        }
    }

    private async void PollForCommitDialogs(object? state)
    {
        if (this.disposed) return;
        
        try
        {
            // Update process list periodically
            this.UpdateTgitProcessList();
            
            // Find all top-level windows for TortoiseGit processes
            foreach (int processId in this.tgitProcessIds)
            {
                try
                {
                    var process = Process.GetProcessById(processId);
                    if (process.HasExited) continue;

                    // Find all windows for this process
                    var windows = AutomationElement.RootElement.FindAll(
                        TreeScope.Children,
                        new PropertyCondition(AutomationElement.ProcessIdProperty, processId));

                    foreach (AutomationElement window in windows)
                    {
                        try
                        {
                            if (window.Current.Name.IndexOf("Commit", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                Console.WriteLine($"\n--- Commit Dialog Found by Polling: {window.Current.Name} ---");

                                if (this.injector.TryFindMessageBox(window, out var messageBox) && messageBox != null)
                                {
                                    await this.ProcessCommitDialog(window, messageBox);
                                    return; // Process one at a time
                                }
                            }
                        }
                        catch (ElementNotAvailableException)
                        {
                            // Window might have closed
                            continue;
                        }
                    }
                }
                catch (ArgumentException)
                {
                    // Process has exited
                    continue;
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error during polling: {ex.Message}");
        }
    }

    private async Task ProcessCommitDialog(AutomationElement commitDialog, AutomationElement messageBox)
    {
        IntPtr dialogHandle;
        try
        {
            dialogHandle = new IntPtr(commitDialog.Current.NativeWindowHandle);
        }
        catch (ElementNotAvailableException)
        {
            return; // Dialog closed before we could get its handle.
        }

        // If we've already decided to process or ignore this specific dialog window, don't try again.
        if (this.processedDialogs.Contains(dialogHandle)) return;

        // Atomically check if another process is running and set the flag.
        if (Interlocked.CompareExchange(ref this.isProcessing, 1, 0) != 0)
        {
            return; // Already processing, so we skip this request.
        }

        // Stop the polling timer while we are busy generating the commit message.
        this.pollingTimer.Change(Timeout.Infinite, Timeout.Infinite);

        try
        {
            // Mark this dialog as "processed" up front. This prevents re-entry from polls
            // or events for this dialog instance, regardless of success or failure below.
            this.processedDialogs.Add(dialogHandle);
            Console.WriteLine($"Processing dialog {dialogHandle} for the first time.");

            string? existingText = this.injector.GetCommitMessage(messageBox);
            if (existingText == null) return;

            this.injector.SetCommitMessage(messageBox, "Generating commit message... [Detecting repository]");
            string? repoRoot = RepoFinder.FindRepoRootFromDialog(commitDialog);

            if (string.IsNullOrEmpty(repoRoot))
            {
                this.injector.SetCommitMessage(messageBox, "Error: Could not determine Git repository root.");
                return;
            }

            this.injector.SetCommitMessage(messageBox, $"Generating commit message... [Repo: {Path.GetFileName(repoRoot)}]");
            string gitContext = await GitDiffHelper.GetDiffAsync(repoRoot);

            if (gitContext.StartsWith("Error:"))
            {
                this.injector.SetCommitMessage(messageBox, gitContext);
                return;
            }

            if (string.IsNullOrWhiteSpace(gitContext))
            {
                this.injector.SetCommitMessage(messageBox, "No changes detected to commit.");
                Console.WriteLine("No changes detected. Aborting AI generation.");
                return;
            }

            string prompt = "You are an expert git commit message generation tool. " +
                     "Based on the following git context (recent commits, changed files, and full diff), create a concise, conventional commit message. " +
                     "The message must have a short subject line (under 50 characters), a blank line, and then a brief bulleted description of the most important changes. " +
                     "Your response MUST be only the raw commit message text. DO NOT include explanations, options, markdown formatting, or placeholders like '[Your ID]'.\n\n" +
                     "Git Context:\n" +
                     "```\n" +
                     $"{gitContext}\n" +
                     "```";

            Console.WriteLine("--- Sending Request to Gemini ---");
            Console.WriteLine($"[Existing Text]: {existingText}");
            Console.WriteLine($"[Git Context Length]: {gitContext.Length} characters");
            Console.WriteLine($"[Generated Prompt]:\n{prompt}");
            Console.WriteLine("---------------------------------");

            string initialMessage = "Generating AI commit message...";
            this.injector.SetCommitMessage(messageBox, initialMessage);
            var stopwatch = Stopwatch.StartNew();

            var apiTask = GeminiApiClient.GenerateCommitMessageAsync(prompt);

            while (!apiTask.IsCompleted)
            {
                await Task.WhenAny(apiTask, Task.Delay(1000));
                if (apiTask.IsCompleted) break;

                try
                {
                    var elapsedSeconds = (int)Math.Round(stopwatch.Elapsed.TotalSeconds);
                    if (elapsedSeconds > 0)
                    {
                        this.injector.SetCommitMessage(messageBox, $"{initialMessage} ({elapsedSeconds}s)");
                    }
                }
                catch (ElementNotAvailableException)
                {
                    Console.WriteLine("Commit dialog closed while generating message. Aborting.");
                    if (this.processedDialogs.Remove(dialogHandle))
                    {
                        Console.WriteLine($"Cleaned up handle {dialogHandle} after dialog closed during generation.");
                    }
                    stopwatch.Stop();
                    return;
                }
            }

            stopwatch.Stop();
            string finalMessage = await apiTask;

            try
            {
                // Re-check if element is available before setting final message.
                _ = messageBox.Current.Name;
            }
            catch (ElementNotAvailableException)
            {
                Console.WriteLine("Commit dialog closed before setting final message.");
                if (this.processedDialogs.Remove(dialogHandle))
                {
                    Console.WriteLine($"Cleaned up handle {dialogHandle} after dialog closed before completion.");
                }
                return;
            }

            if (finalMessage.Contains("[") && (finalMessage.Contains("Insert") || finalMessage.Contains("Issue") || finalMessage.Contains("Your ")))
            {
                Console.WriteLine("!!! WARNING: AI returned a template. Falling back to a default message. !!!");
                finalMessage = "chore: Minor update";
            }

            this.injector.SetCommitMessage(messageBox, finalMessage.Trim());

            Console.WriteLine($"Message set for handle {dialogHandle}. Waiting for next event...");
        }
        finally
        {
            // Restart the polling timer.
            if (!this.disposed)
            {
                this.pollingTimer.Change(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
            }
            // Release the processing flag.
            Interlocked.Exchange(ref this.isProcessing, 0);
        }
    }

    public void Dispose()
    {
        if (this.disposed) return;
        this.disposed = true;

        this.pollingTimer.Dispose();

        try { Automation.RemoveAutomationEventHandler(WindowPattern.WindowOpenedEvent, AutomationElement.RootElement, this.windowOpenedHandler); }
        catch (Exception) { /* Best effort on shutdown */ }

        try { Automation.RemoveAutomationEventHandler(WindowPattern.WindowClosedEvent, AutomationElement.RootElement, this.windowClosedHandler); }
        catch (Exception) { /* Best effort on shutdown */ }

        GC.SuppressFinalize(this);
    }
}
#endregion


public class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        var injector = new TortoiseGitInjector();
        using var watcher = new CommitDialogWatcher(injector);

        watcher.Start();

        Console.WriteLine("Monitoring is active. Press Enter to exit.");
        Console.ReadLine();
    }
}

#if NET472
#pragma warning restore CS8632
#endif
