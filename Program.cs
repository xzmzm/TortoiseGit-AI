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
            _processedDialogHandle = 0;
            return false;
        }

        AutomationElementCollection topLevelWindows = AutomationElement.RootElement.FindAll(TreeScope.Children, Condition.TrueCondition);
        foreach (AutomationElement candidateWindow in topLevelWindows)
        {
            try
            {
                int currentHandle = candidateWindow.Current.NativeWindowHandle;
                if (currentHandle == _processedDialogHandle && currentHandle != 0) continue;

                if (tgitProcessIds.Contains(candidateWindow.Current.ProcessId) && candidateWindow.Current.Name.Contains("Commit", StringComparison.OrdinalIgnoreCase))
                {
                    var textCondition = new PropertyCondition(AutomationElement.ClassNameProperty, COMMIT_TEXTBOX_CLASS_NAME);
                    var foundMessageBox = candidateWindow.FindFirst(TreeScope.Descendants, textCondition);
                    if (foundMessageBox != null)
                    {
                        commitDialog = candidateWindow;
                        messageBox = foundMessageBox;
                        _processedDialogHandle = currentHandle;
                        return true;
                    }
                }
            }
            catch (ElementNotAvailableException)
            {
                if (candidateWindow.Current.NativeWindowHandle == _processedDialogHandle) { _processedDialogHandle = 0; }
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
            // First, check for any changes. If not, no context is needed.
            var (statusOutput, statusError, statusExitCode) = await RunGitCommandAsync("status --short", workingDirectory);
            if (statusExitCode != 0)
            {
                Console.WriteLine($"Warning: 'git status' command failed with exit code {statusExitCode}:\n{statusError}");
            }

            if (string.IsNullOrWhiteSpace(statusOutput))
            {
                return string.Empty; // No changes found, return empty string to trigger rewrite/placeholder logic.
            }

            var fullContext = new StringBuilder();

            // 1. Recent commits for historical context
            var (logOutput, logError, logExitCode) = await RunGitCommandAsync("log -n 5 --oneline --no-decorate", workingDirectory);
            if (logExitCode == 0 && !string.IsNullOrWhiteSpace(logOutput))
            {
                fullContext.AppendLine("Recent commits:");
                fullContext.AppendLine(logOutput);
                fullContext.AppendLine();
            }
            else if (logExitCode != 0) { Console.WriteLine($"Warning: 'git log' failed with exit code {logExitCode}:\n{logError}"); }

            // 2. Changed files for a high-level overview
            fullContext.AppendLine("Changed files:");
            fullContext.AppendLine(statusOutput); // Re-use status output from our initial check
            fullContext.AppendLine();

            fullContext.AppendLine("Full diff:");

            // 3. Unstaged changes
            var (diffOutput, diffError, diffExitCode) = await RunGitCommandAsync("diff HEAD", workingDirectory);
            if (diffExitCode != 0) return $"Error: 'git diff HEAD' failed.\n{diffError}";
            if (!string.IsNullOrWhiteSpace(diffOutput)) fullContext.AppendLine(diffOutput);

            // 4. Staged changes (what will be committed)
            var (cachedDiffOutput, cachedDiffError, cachedDiffExitCode) = await RunGitCommandAsync("diff --cached HEAD", workingDirectory);
            if (cachedDiffExitCode != 0) return $"Error: 'git diff --cached HEAD' failed.\n{cachedDiffError}";
            if (!string.IsNullOrWhiteSpace(cachedDiffOutput)) fullContext.AppendLine(cachedDiffOutput);

            //// 5. Untracked files, treated as new files in the diff
            //var (untrackedFilesOutput, untrackedFilesError, untrackedFilesExitCode) = await RunGitCommandAsync("ls-files --others --exclude-standard", workingDirectory);
            //if (untrackedFilesExitCode == 0 && !string.IsNullOrWhiteSpace(untrackedFilesOutput))
            //{
            //    var untrackedFiles = untrackedFilesOutput.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
            //    foreach (var file in untrackedFiles)
            //    {
            //        // Git on Windows understands /dev/null. Quote files with spaces.
            //        string escapedFile = file.Contains(' ') ? $"\"{file}\"" : file;
            //        var (untrackedDiffOutput, untrackedDiffError, untrackedDiffExitCode) = await RunGitCommandAsync($"diff --no-index /dev/null {escapedFile}", workingDirectory);

            //        // For `git diff`, exit code 1 means there are differences, which is not an error here.
            //        if (untrackedDiffExitCode > 1)
            //        {
            //            Console.WriteLine($"Warning: 'git diff' for untracked file '{file}' failed with exit code {untrackedDiffExitCode}:\n{untrackedDiffError}");
            //        }
            //        if (!string.IsNullOrWhiteSpace(untrackedDiffOutput)) fullContext.AppendLine(untrackedDiffOutput);
            //    }
            //}
            //else if (untrackedFilesExitCode != 0)
            //{
            //    Console.WriteLine($"Warning: 'git ls-files' failed with exit code {untrackedFilesExitCode}:\n{untrackedFilesError}");
            //}

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
            return startingPath;  //FindGitRootFromPath(startingPath);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error finding repository root: {ex.Message}");
            return null;
        }
    }

    private static string? GetPathFromProcess(int processId)
    {
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
    private static void ApplicationLoop()
    {
        var injector = new TortoiseGitInjector();
        Console.WriteLine("Monitoring for TortoiseGit commit dialog... (Press Ctrl+C to exit)");

        while (true)
        {
            if (injector.TryFindNewCommitDialog(out var commitDialog, out var messageBox) && commitDialog != null && messageBox != null)
            {
                Console.WriteLine("\n--- New Commit Dialog Found ---");
                string? existingText = injector.GetCommitMessage(messageBox);
                if (existingText == null) continue;

                injector.SetCommitMessage(messageBox, "Generating commit message... [Detecting repository]");
                string? repoRoot = RepoFinder.FindRepoRootFromDialog(commitDialog);

                if (string.IsNullOrEmpty(repoRoot))
                {
                    injector.SetCommitMessage(messageBox, "Error: Could not determine Git repository root.");
                    Thread.Sleep(3000); continue;
                }

                injector.SetCommitMessage(messageBox, $"Generating commit message... [Repo: {Path.GetFileName(repoRoot)}]");
                string gitContext = GitDiffHelper.GetDiffAsync(repoRoot).GetAwaiter().GetResult();
                string prompt;

                // *** HYPER-SPECIFIC PROMPT ENGINEERING ***
                if (gitContext.StartsWith("Error:"))
                {
                    injector.SetCommitMessage(messageBox, gitContext);
                    Thread.Sleep(3000); continue;
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

                // *** ADDED DEBUGGING LOGS ***
                Console.WriteLine("--- Sending Request to Gemini ---");
                Console.WriteLine($"[Existing Text]: {existingText}");
                Console.WriteLine($"[Git Context Length]: {gitContext.Length} characters");
                Console.WriteLine($"[Generated Prompt]:\n{prompt}");
                Console.WriteLine("---------------------------------");

                string finalMessage = GeminiApiClient.GenerateCommitMessageAsync(prompt).GetAwaiter().GetResult();

                // *** ADDED SAFETY NET / SANITIZATION ***
                if (finalMessage.Contains("[") && (finalMessage.Contains("Insert") || finalMessage.Contains("Issue") || finalMessage.Contains("Your ")))
                {
                    Console.WriteLine("!!! WARNING: AI returned a template. Falling back to a default message. !!!");
                    finalMessage = "chore: Minor update";
                }

                injector.SetCommitMessage(messageBox, finalMessage.Trim());

                Console.WriteLine($"Message set. Monitoring for next dialog...");
            }
            Thread.Sleep(500);
        }
    }

    public static void Main(string[] args)
    {
        var appThread = new Thread(ApplicationLoop);
        appThread.SetApartmentState(ApartmentState.STA);
        appThread.Start();
        appThread.Join();
    }
}