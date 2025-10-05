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
    public static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);
    public const uint WM_PASTE = 0x0302;
    public const uint SCI_SELECTALL = 2013;
}

public class TortoiseGitInjector
{
    private const string COMMIT_TEXTBOX_CLASS_NAME = "Scintilla";

    public bool TryFindMessageBox(AutomationElement commitDialog, out AutomationElement? messageBox)
    {
        var textCondition = new PropertyCondition(AutomationElement.ClassNameProperty, COMMIT_TEXTBOX_CLASS_NAME);
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
                NativeMethods.SendMessage(hwnd, NativeMethods.SCI_SELECTALL, IntPtr.Zero, IntPtr.Zero);
                originalClipboardData = Clipboard.GetDataObject();
                Clipboard.SetText(text);
                NativeMethods.SendMessage(hwnd, NativeMethods.WM_PASTE, IntPtr.Zero, IntPtr.Zero);
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
    private static readonly HttpClient _httpClient = new HttpClient();
    private const string ApiEndpoint = "https://generativelanguage.googleapis.com/v1beta/models/gemini-flash-lite-latest:generateContent";

    static GeminiApiClient() { _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json")); }

    public static async Task<string> GenerateCommitMessageAsync(string prompt)
    {
        string? apiKey = Environment.GetEnvironmentVariable("TORTOISEGIT_GEMINI_API_KEY");
        if (string.IsNullOrEmpty(apiKey)) return "Error: TORTOISEGIT_GEMINI_API_KEY environment variable not set.";

        var requestBody = new GeminiRequest { Contents = new[] { new Content { Parts = new[] { new Part { Text = prompt } } } } };
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Post, $"{ApiEndpoint}?key={apiKey}");
            request.Content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json");
            HttpResponseMessage response = await _httpClient.SendAsync(request);
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
            var (statusOutput, statusError, statusExitCode) = await RunGitCommandAsync("status --short", workingDirectory);
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

            var (diffOutput, diffError, diffExitCode) = await RunGitCommandAsync("diff HEAD", workingDirectory);
            if (diffExitCode != 0) return $"Error: 'git diff HEAD' failed.\n{diffError}";
            if (!string.IsNullOrWhiteSpace(diffOutput)) fullContext.AppendLine(diffOutput);

            var (cachedDiffOutput, cachedDiffError, cachedDiffExitCode) = await RunGitCommandAsync("diff --cached HEAD", workingDirectory);
            if (cachedDiffExitCode != 0) return $"Error: 'git diff --cached HEAD' failed.\n{cachedDiffError}";
            if (!string.IsNullOrWhiteSpace(cachedDiffOutput)) fullContext.AppendLine(cachedDiffOutput);

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
    private readonly TortoiseGitInjector _injector;
    private readonly AutomationEventHandler _windowOpenedHandler;
    private readonly HashSet<int> _tgitProcessIds = new HashSet<int>();

    public CommitDialogWatcher(TortoiseGitInjector injector)
    {
        _injector = injector;
        // The handler delegate is stored in a field to prevent it from being garbage collected
        _windowOpenedHandler = OnWindowOpened;
    }

    public void Start()
    {
        Console.WriteLine("Starting event-driven monitoring for TortoiseGit commit dialog...");
        UpdateTgitProcessList();
        Automation.AddAutomationEventHandler(
            WindowPattern.WindowOpenedEvent,
            AutomationElement.RootElement,
            TreeScope.Children,
            _windowOpenedHandler);
    }

    private void UpdateTgitProcessList()
    {
        _tgitProcessIds.Clear();
        foreach (var p in Process.GetProcessesByName("TortoiseGitProc"))
        {
            _tgitProcessIds.Add(p.Id);
        }
    }

    private async void OnWindowOpened(object sender, AutomationEventArgs e)
    {
        if (sender is not AutomationElement openedWindow) return;

        try
        {
            // Refresh the process list in case TortoiseGitProc just started with this dialog
            UpdateTgitProcessList();

            // Quick filters to ignore irrelevant windows
            if (!_tgitProcessIds.Contains(openedWindow.Current.ProcessId)) return;
            if (!openedWindow.Current.Name.Contains("Commit", StringComparison.OrdinalIgnoreCase)) return;

            Console.WriteLine("\n--- Potential Commit Dialog Found by Event ---");

            // Use existing logic to find the specific message box control
            if (_injector.TryFindMessageBox(openedWindow, out var messageBox) && messageBox != null)
            {
                // We found it! Process it asynchronously to avoid blocking the UI event handler thread.
                await ProcessCommitDialog(openedWindow, messageBox);
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
        string? existingText = _injector.GetCommitMessage(messageBox);
        if (existingText == null) return;

        _injector.SetCommitMessage(messageBox, "Generating commit message... [Detecting repository]");
        string? repoRoot = RepoFinder.FindRepoRootFromDialog(commitDialog);

        if (string.IsNullOrEmpty(repoRoot))
        {
            _injector.SetCommitMessage(messageBox, "Error: Could not determine Git repository root.");
            return;
        }

        _injector.SetCommitMessage(messageBox, $"Generating commit message... [Repo: {Path.GetFileName(repoRoot)}]");
        string gitContext = await GitDiffHelper.GetDiffAsync(repoRoot);
        string prompt;

        if (gitContext.StartsWith("Error:"))
        {
            _injector.SetCommitMessage(messageBox, gitContext);
            return;
        }

        if (string.IsNullOrWhiteSpace(gitContext))
        {
            if (!string.IsNullOrWhiteSpace(existingText))
            {
                prompt = "You are an automated tool that rewrites Git commit messages to follow the conventional commit standard. " +
                         "Take the user's draft and output a complete, industry-standard commit message. " +
                         "Your response MUST be only the raw commit message. DO NOT use placeholders like '[Insert Issue Number/ID]'. DO NOT add any commentary or explanation.\n\n" +
                         $"User's draft: '{existingText}'";
            }
            else
            {
                prompt = "You are an automated tool that generates Git commit messages. " +
                         "Generate a complete and usable conventional commit message for a minor, unspecified change. Example: 'chore: Minor code cleanup'. " +
                         "Your response MUST be only the raw commit message. DO NOT use placeholders or provide any explanation.";
            }
        }
        else
        {
            prompt = "You are an expert git commit message generation tool. " +
                     "Based on the following git context (recent commits, changed files, and full diff), create a concise, conventional commit message. " +
                     "The message must have a short subject line (under 50 characters), a blank line, and then a brief bulleted description of the most important changes. " +
                     "Your response MUST be only the raw commit message text. DO NOT include explanations, options, markdown formatting, or placeholders like '[Your ID]'.\n\n" +
                     "Git Context:\n" +
                     "```\n" +
                     $"{gitContext}\n" +
                     "```";
        }

        Console.WriteLine("--- Sending Request to Gemini ---");
        Console.WriteLine($"[Existing Text]: {existingText}");
        Console.WriteLine($"[Git Context Length]: {gitContext.Length} characters");
        Console.WriteLine($"[Generated Prompt]:\n{prompt}");
        Console.WriteLine("---------------------------------");

        string finalMessage = await GeminiApiClient.GenerateCommitMessageAsync(prompt);

        if (finalMessage.Contains("[") && (finalMessage.Contains("Insert") || finalMessage.Contains("Issue") || finalMessage.Contains("Your ")))
        {
            Console.WriteLine("!!! WARNING: AI returned a template. Falling back to a default message. !!!");
            finalMessage = "chore: Minor update";
        }

        _injector.SetCommitMessage(messageBox, finalMessage.Trim());

        Console.WriteLine($"Message set. Waiting for next event...");
    }

    public void Dispose()
    {
        Automation.RemoveAutomationEventHandler(WindowPattern.WindowOpenedEvent, AutomationElement.RootElement, _windowOpenedHandler);
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
