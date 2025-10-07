using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
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
        // AutomationElement must be used on the thread it was created on (UIA event thread, which is MTA).
        // Clipboard operations must happen on an STA thread.
        // So, we get the window handle here, then perform clipboard actions on a new STA thread.
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

public class GeminiRequest { [JsonPropertyName("contents")] public Content[] Contents { get; set; } }
public class GeminiResponse { [JsonPropertyName("candidates")] public Candidate[]? Candidates { get; set; } }
public class Candidate { [JsonPropertyName("content")] public Content? Content { get; set; } }
public class Content { [JsonPropertyName("parts")] public Part[]? Parts { get; set; } }
public class Part { [JsonPropertyName("text")] public string Text { get; set; } }
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

        await process.WaitForExitAsync();

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
                Console.WriteLine($"Warning: Diff is larger than {MaxDiffBytes / 1024}KB, truncating for prompt.");

                // Start with a proportional guess to make the loop faster
                int len = (int)(combinedDiff.Length * ((double)MaxDiffBytes / Encoding.UTF8.GetByteCount(combinedDiff)));

                // Reduce length until it's safely under the byte limit
                while (Encoding.UTF8.GetByteCount(combinedDiff.Substring(0, len)) > MaxDiffBytes) { len--; }

                // Find last newline to avoid cutting a line
                int cutIndex = combinedDiff.LastIndexOf('\n', len > 0 ? len - 1 : 0);
                if (cutIndex <= 0) cutIndex = len;

                string truncatedDiff = combinedDiff.Substring(0, cutIndex);
                fullContext.AppendLine(truncatedDiff);
                fullContext.AppendLine("\n[... Diff truncated due to size ...]");
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
    private readonly HashSet<int> tgitProcessIds = new HashSet<int>();

    public CommitDialogWatcher(TortoiseGitInjector injector)
    {
        this.injector = injector;
        // The handler delegate is stored in a field to prevent it from being garbage collected
        this.windowOpenedHandler = this.OnWindowOpened;
    }

    public void Start()
    {
        Console.WriteLine("Starting event-driven monitoring for TortoiseGit commit dialog...");
        this.UpdateTgitProcessList();
        Automation.AddAutomationEventHandler(
            WindowPattern.WindowOpenedEvent,
            AutomationElement.RootElement,
            TreeScope.Children,
            this.windowOpenedHandler);
    }

    private void UpdateTgitProcessList()
    {
        this.tgitProcessIds.Clear();
        foreach (var p in Process.GetProcessesByName("TortoiseGitProc"))
        {
            this.tgitProcessIds.Add(p.Id);
        }
    }

    private async void OnWindowOpened(object sender, AutomationEventArgs e)
    {
        if (sender is not AutomationElement openedWindow) return;

        try
        {
            // Refresh the process list in case TortoiseGitProc just started with this dialog
            this.UpdateTgitProcessList();

            // Quick filters to ignore irrelevant windows
            if (!this.tgitProcessIds.Contains(openedWindow.Current.ProcessId)) return;
            if (!openedWindow.Current.Name.Contains("Commit", StringComparison.OrdinalIgnoreCase)) return;

            Console.WriteLine("\n--- Potential Commit Dialog Found by Event ---");

            if (this.injector.TryFindMessageBox(openedWindow, out var messageBox) && messageBox != null)
            {
                // We found it! Process it asynchronously to avoid blocking the UI event handler thread.
                await this.ProcessCommitDialog(openedWindow, messageBox);
            }
        }
        catch (ElementNotAvailableException)
        {
            // The window might have closed very quickly, which is fine.
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error in event handler: {ex.Message}");
        }
    }

    private async Task ProcessCommitDialog(AutomationElement commitDialog, AutomationElement messageBox)
    {
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
            return;
        }

        if (finalMessage.Contains("[") && (finalMessage.Contains("Insert") || finalMessage.Contains("Issue") || finalMessage.Contains("Your ")))
        {
            Console.WriteLine("!!! WARNING: AI returned a template. Falling back to a default message. !!!");
            finalMessage = "chore: Minor update";
        }

        this.injector.SetCommitMessage(messageBox, finalMessage.Trim());

        Console.WriteLine($"Message set. Waiting for next event...");
    }

    public void Dispose()
    {
        Automation.RemoveAutomationEventHandler(WindowPattern.WindowOpenedEvent, AutomationElement.RootElement, this.windowOpenedHandler);
        GC.SuppressFinalize(this);
    }
}
#endregion

#region Process Creation Watcher
public class ProcessCreationWatcher : IDisposable
{
    private readonly ManagementEventWatcher watcher;
    private const string ProcessName = "TortoiseGitProc.exe";

    public ProcessCreationWatcher()
    {
        string query = $"SELECT * FROM __InstanceCreationEvent WITHIN 1 WHERE TargetInstance ISA 'Win32_Process' AND TargetInstance.Name = '{ProcessName}'";
        this.watcher = new ManagementEventWatcher(new WqlEventQuery(query));
        this.watcher.EventArrived += this.OnProcessStarted;
    }

    public void Start()
    {
        Console.WriteLine($"Starting to watch for {ProcessName} launches to report arguments...");
        this.watcher.Start();
    }

    public void Stop()
    {
        this.watcher.Stop();
    }

    private void OnProcessStarted(object sender, EventArrivedEventArgs e)
    {
        try
        {
            if (e.NewEvent["TargetInstance"] is not ManagementBaseObject targetInstance) { return; }

            var commandLineObj = targetInstance["CommandLine"];
            if (commandLineObj is not string commandLine || string.IsNullOrEmpty(commandLine)) { return; }

            var processId = (uint)targetInstance["ProcessId"];

            Console.WriteLine("\n--- TortoiseGitProc Launched ---");
            Console.WriteLine($"Process ID:   {processId}");
            Console.WriteLine($"Command Line: {commandLine}");

            var pathFileMatch = Regex.Match(commandLine, @"/pathfile:""([^""]+)""");
            if (pathFileMatch.Success)
            {
                string pathFile = pathFileMatch.Groups[1].Value;
                Console.WriteLine($"-> Found pathfile: {pathFile}");

                List<IntPtr> threadHandles = null;
                try
                {
                    // Suspend the process to prevent it from deleting the file
                    threadHandles = this.SuspendProcess((int)processId);
                    if (threadHandles.Count == 0)
                    {
                        Console.WriteLine("-> Could not suspend process; it may have exited too quickly.");
                    }
                    else
                    {
                        Console.WriteLine($"-> Process {processId} suspended with {threadHandles.Count} threads.");

                        // Give a tiny moment for the file system to catch up, just in case.
                        Thread.Sleep(50);

                        if (File.Exists(pathFile))
                        {
                            var fileContent = File.ReadAllText(pathFile, Encoding.UTF8);
                            Console.WriteLine("--- Pathfile Content ---");
                            Console.WriteLine(fileContent);
                            Console.WriteLine("------------------------");
                        }
                        else
                        {
                            Console.WriteLine("-> Pathfile was not found after suspending the process.");
                        }
                    }
                }
                finally
                {
                    if (threadHandles != null && threadHandles.Count > 0)
                    {
                        this.ResumeProcess(threadHandles);
                        Console.WriteLine($"-> Process {processId} resumed.");
                    }
                }
            }

            Console.WriteLine("--------------------------------\n");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error processing process start event: {ex.Message}");
        }
    }

    private List<IntPtr> SuspendProcess(int processId)
    {
        var threadHandles = new List<IntPtr>();
        try
        {
            var process = Process.GetProcessById(processId);
            foreach (ProcessThread pT in process.Threads)
            {
                IntPtr pOpenThread = NativeMethods.OpenThread(NativeMethods.ThreadAccess.SuspendResume, false, (uint)pT.Id);
                if (pOpenThread != IntPtr.Zero)
                {
                    NativeMethods.SuspendThread(pOpenThread);
                    threadHandles.Add(pOpenThread);
                }
            }
        }
        catch (ArgumentException)
        {
            // Process may have already exited
            Console.WriteLine($"-> Process {processId} exited before it could be suspended.");
            foreach (var handle in threadHandles) { NativeMethods.CloseHandle(handle); }
            return new List<IntPtr>();
        }
        return threadHandles;
    }

    private void ResumeProcess(List<IntPtr> threadHandles)
    {
        foreach (var handle in threadHandles)
        {
            NativeMethods.ResumeThread(handle);
            NativeMethods.CloseHandle(handle);
        }
    }

    public void Dispose()
    {
        this.Stop();
        this.watcher.Dispose();
        GC.SuppressFinalize(this);
    }
}
#endregion

#region Temp File Watcher
public class TempFileWatcher : IDisposable
{
    private readonly FileSystemWatcher fileWatcher;
    private readonly string watchPath;

    public TempFileWatcher()
    {
        this.watchPath = Path.Combine(Path.GetTempPath(), "tortoisegit");
        try
        {
            if (!Directory.Exists(this.watchPath))
            {
                Console.WriteLine($"Creating monitored directory: {this.watchPath}");
                Directory.CreateDirectory(this.watchPath);
            }

            this.fileWatcher = new FileSystemWatcher(this.watchPath)
            {
                NotifyFilter = NotifyFilters.FileName, // Just need to know when a file is created.
                IncludeSubdirectories = false
            };
            this.fileWatcher.Created += this.OnFileCreated;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FATAL: Could not initialize TempFileWatcher for path '{this.watchPath}'. Error: {ex.Message}");
            this.fileWatcher = null; // Ensure watcher is null so Start() doesn't throw.
        }
    }

    public void Start()
    {
        if (this.fileWatcher == null) return;
        Console.WriteLine($"Watching for temp files in: {this.watchPath}");
        this.fileWatcher.EnableRaisingEvents = true;
    }

    public void Stop()
    {
        if (this.fileWatcher != null)
        {
            this.fileWatcher.EnableRaisingEvents = false;
        }
    }

    private void OnFileCreated(object sender, FileSystemEventArgs e)
    {
        Console.WriteLine($"\n--- Temp File Detected: {e.Name} ---");
        // Use a small, quick retry loop to handle the race condition where the file is created but not yet written/unlocked.
        for (int i = 0; i < 5; i++)
        {
            try
            {
                // A small delay on the first try can be effective as the create event may fire before the write completes.
                if (i == 0) Thread.Sleep(10);

                string content = File.ReadAllText(e.FullPath, Encoding.UTF8);
                Console.WriteLine("--- Pathfile Content (from FileSystemWatcher) ---");
                Console.WriteLine(content);
                Console.WriteLine("-------------------------------------------------");
                return; // Success, exit the method.
            }
            catch (FileNotFoundException) { Console.WriteLine("-> File disappeared before it could be read."); break; }
            catch (IOException) { if (i < 4) Thread.Sleep(20); } // File is likely locked. Wait and try again.
            catch (Exception ex) { Console.WriteLine($"-> An unexpected error occurred while reading temp file: {ex.Message}"); break; }
        }
        Console.WriteLine("-> Could not read file content after multiple attempts.");
        Console.WriteLine("---------------------------------------\n");
    }

    public void Dispose()
    {
        this.Stop();
        this.fileWatcher?.Dispose();
        GC.SuppressFinalize(this);
    }
}
#endregion


public class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        using var processWatcher = new ProcessCreationWatcher();
        processWatcher.Start();

        using var tempFileWatcher = new TempFileWatcher();
        tempFileWatcher.Start();

        var injector = new TortoiseGitInjector();
        using var watcher = new CommitDialogWatcher(injector);

        watcher.Start();

        Console.WriteLine("Monitoring is active. Press Enter to exit.");
        Console.ReadLine();
    }
}
