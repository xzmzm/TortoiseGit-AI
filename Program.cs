using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
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
    private int _processedDialogHandle = 0;

    public bool TryFindNewCommitDialog(out AutomationElement? commitDialog, out AutomationElement? messageBox)
    {
        commitDialog = null;
        messageBox = null;

        var tgitProcessIds = Process.GetProcessesByName("TortoiseGitProc").Select(p => p.Id).ToHashSet();
        if (tgitProcessIds.Count == 0)
        {
            _processedDialogHandle = 0; // Reset if all processes are gone
            return false;
        }

        AutomationElementCollection topLevelWindows = AutomationElement.RootElement.FindAll(TreeScope.Children, Condition.TrueCondition);
        foreach (AutomationElement candidateWindow in topLevelWindows)
        {
            try
            {
                int currentHandle = candidateWindow.Current.NativeWindowHandle;
                if (currentHandle == _processedDialogHandle) continue; // Skip the one we just handled

                if (tgitProcessIds.Contains(candidateWindow.Current.ProcessId) && candidateWindow.Current.Name.Contains("Commit", StringComparison.OrdinalIgnoreCase))
                {
                    var textCondition = new PropertyCondition(AutomationElement.ClassNameProperty, COMMIT_TEXTBOX_CLASS_NAME);
                    var foundMessageBox = candidateWindow.FindFirst(TreeScope.Descendants, textCondition);
                    if (foundMessageBox != null)
                    {
                        commitDialog = candidateWindow;
                        messageBox = foundMessageBox;
                        _processedDialogHandle = currentHandle; // Mark this handle as processed
                        return true;
                    }
                }
            }
            catch (ElementNotAvailableException)
            {
                _processedDialogHandle = 0; // Window disappeared, so reset
                continue;
            }
        }
        return false;
    }

    public string? GetCommitMessage(AutomationElement messageBox)
    {
        try { return messageBox.Current.Name; }
        catch (ElementNotAvailableException) { return null; }
    }

    public bool SetCommitMessage(AutomationElement messageBox, string text)
    {
        IDataObject? originalClipboardData = null;
        try
        {
            IntPtr hwnd = new IntPtr(messageBox.Current.NativeWindowHandle);
            messageBox.SetFocus();
            NativeMethods.SendMessage(hwnd, NativeMethods.SCI_SELECTALL, 0, 0);
            originalClipboardData = Clipboard.GetDataObject();
            Clipboard.SetText(text);
            NativeMethods.SendMessage(hwnd, NativeMethods.WM_PASTE, IntPtr.Zero, IntPtr.Zero);
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error during clipboard paste operation: {ex.Message}");
            return false;
        }
        finally
        {
            if (originalClipboardData != null)
            {
                try { Clipboard.SetDataObject(originalClipboardData, true, 5, 100); }
                catch (Exception clipEx) { Console.WriteLine($"Failed to restore clipboard: {clipEx.Message}"); }
            }
        }
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
        string? apiKey = Environment.GetEnvironmentVariable("GEMINI_API_KEY");
        if (string.IsNullOrEmpty(apiKey)) return "Error: GEMINI_API_KEY environment variable not set.";

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
    public static async Task<string> GetStagedDiffAsync(string workingDirectory)
    {
        var processStartInfo = new ProcessStartInfo
        {
            FileName = "git",
            Arguments = "diff --staged",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            WorkingDirectory = workingDirectory
        };
        try
        {
            using var process = new Process { StartInfo = processStartInfo };
            process.Start();
            Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
            Task<string> errorTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            string output = await outputTask;
            string error = await errorTask;
            if (process.ExitCode != 0) return $"Error: Git command failed with exit code {process.ExitCode}.\n{error}";
            return output;
        }
        catch (Win32Exception) { return "Error: 'git' command not found. Ensure Git is installed and in your system's PATH."; }
        catch (Exception ex) { return $"Error: An unexpected exception occurred while running git. {ex.Message}"; }
    }
}

public static class RepoFinder
{
    public static string? FindRepoRootFromDialog(AutomationElement commitDialog)
    {
        try
        {
            int processId = commitDialog.Current.ProcessId;
            // This is now a synchronous call that internally uses Task.Run
            string? startingPath = GetPathFromProcess(processId);

            if (string.IsNullOrEmpty(startingPath))
            {
                Console.WriteLine("Could not find path from command line, trying window title as fallback.");
                // **FIXED REGEX**: This robustly finds a Windows path in the title.
                var match = Regex.Match(commitDialog.Current.Name, @"([A-Z]:\\[^-\r\n]+)");
                if (match.Success)
                {
                    startingPath = match.Groups[1].Value.Trim();
                }
            }

            if (string.IsNullOrEmpty(startingPath))
            {
                Console.WriteLine($"Could not determine a starting path from title: '{commitDialog.Current.Name}'");
                return null;
            }

            Console.WriteLine($"Found starting path: {startingPath}");
            return FindGitRootFromPath(startingPath);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error finding repository root: {ex.Message}");
            return null;
        }
    }

    private static string? GetPathFromProcess(int processId)
    {
        // We use Task.Run to push the WMI query to a background thread,
        // then block the main STA thread for the result. This is safe.
        return Task.Run(() =>
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
        }).GetAwaiter().GetResult();
    }

    private static string? FindGitRootFromPath(string path)
    {
        DirectoryInfo? currentDir = Directory.Exists(path) ? new DirectoryInfo(path) : Directory.GetParent(path);
        while (currentDir != null)
        {
            if (Directory.Exists(Path.Combine(currentDir.FullName, ".git")))
            {
                Console.WriteLine($"Found Git repository root at: {currentDir.FullName}");
                return currentDir.FullName;
            }
            currentDir = currentDir.Parent;
        }
        Console.WriteLine($"Failed to find a .git directory in any parent folder starting from '{path}'.");
        return null;
    }
}
#endregion

public class Program
{
    // **FIXED**: The main loop is now a synchronous void method.
    private static void ApplicationLoop()
    {
        var injector = new TortoiseGitInjector();
        Console.WriteLine("Monitoring for TortoiseGit commit dialog... (Press Ctrl+C to exit)");

        while (true)
        {
            // All code inside this loop now runs on the main STA thread.
            if (injector.TryFindNewCommitDialog(out var commitDialog, out var messageBox) && commitDialog != null && messageBox != null)
            {
                Console.WriteLine("New commit dialog found. Processing...");
                string? existingText = injector.GetCommitMessage(messageBox);

                if (existingText == null) continue; // Dialog might be closing

                injector.SetCommitMessage(messageBox, "Generating commit message... [Detecting repository]");

                string? repoRoot = RepoFinder.FindRepoRootFromDialog(commitDialog);

                if (string.IsNullOrEmpty(repoRoot))
                {
                    injector.SetCommitMessage(messageBox, "Error: Could not determine the Git repository root.");
                    Thread.Sleep(3000);
                    continue;
                }

                injector.SetCommitMessage(messageBox, $"Generating commit message... [Repo: {Path.GetFileName(repoRoot)}]");

                // **FIXED**: Block the STA thread while async I/O operations complete.
                string gitDiff = GitDiffHelper.GetStagedDiffAsync(repoRoot).GetAwaiter().GetResult();
                string prompt;

                if (gitDiff.StartsWith("Error:"))
                {
                    injector.SetCommitMessage(messageBox, gitDiff);
                    Thread.Sleep(3000);
                    continue;
                }

                if (string.IsNullOrWhiteSpace(gitDiff))
                {
                    prompt = !string.IsNullOrWhiteSpace(existingText)
                       ? $"Refine the following Git commit message to follow conventional commit standards. Make it clear and concise. Original message: '{existingText}'"
                       : "There are no staged changes. Generate a generic placeholder commit message for a small fix or documentation update, following conventional commit standards.";
                }
                else
                {
                    prompt = "You are an expert programmer who writes excellent, concise, and descriptive commit messages.\n" +
                             "Based on the following git diff, generate a commit message following the conventional commit specification.\n" +
                             "The message must have a short subject line (under 50 characters), a blank line, and then a brief bulleted description of the most important changes.\n\n" +
                             "Here is the diff:\n\n" +
                             "```diff\n" +
                             $"{gitDiff}\n" +
                             "```";
                }

                // **FIXED**: Block the STA thread for the API call.
                string finalMessage = GeminiApiClient.GenerateCommitMessageAsync(prompt).GetAwaiter().GetResult();
                injector.SetCommitMessage(messageBox, finalMessage);

                Console.WriteLine("Message set. Monitoring for next dialog...");
            }
            Thread.Sleep(500); // Replaced Task.Delay with Thread.Sleep
        }
    }

    public static void Main(string[] args)
    {
        var appThread = new Thread(() =>
        {
            // **FIXED**: Call the synchronous ApplicationLoop method.
            ApplicationLoop();
        });

        appThread.SetApartmentState(ApartmentState.STA);
        appThread.Start();
        appThread.Join();
    }
}