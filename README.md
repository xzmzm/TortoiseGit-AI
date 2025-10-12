# TortoiseGitAI

TortoiseGitAI is a smart tool that automatically generates descriptive commit messages for your changes using AI. It integrates seamlessly with [TortoiseGit](https://tortoisegit.org/) by monitoring for the commit dialog and using Google's Gemini AI to suggest a message based on your staged changes.

![Demo GIF](https://github.com/user-attachments/assets/51490335-8d53-4040-b092-a3d8dab61a1e)

## Features

- **Automatic Detection**: Runs in the background and automatically detects when the TortoiseGit commit window opens.
- **AI-Powered Suggestions**: Uses Google's Gemini Flash Lite model to analyze your code changes (`git diff`) and generate a conventional commit message.
- **Context-Aware**: Considers recent commit history, staged files, and full diff content to create relevant messages.
- **Seamless Integration**: Injects the generated commit message directly into the TortoiseGit commit message box.
- **Zero Configuration (Almost)**: Just set one environment variable, and you're good to go.
- **Single Executable**: No installation required. The .NET Framework 4.7.2 version is a single `.exe` file with no external dependencies, compatible with Windows 10 and 11 out of the box.

## Requirements

- **Windows 10/11**: The tool is built for Windows and relies on UI Automation.
- **.NET Framework 4.7.2**: This version is pre-installed on modern Windows systems, so you don't need to install anything extra for the `net472` executable.
- **TortoiseGit**: Must be installed and configured.
- **Git**: Must be installed and accessible via the system's `PATH`.

## Installation

1.  **Download the Executable**:

    - Go to the [**Releases**](https://github.com/your-username/TortoiseGitAI/releases) page.
    - You will find two versions available for download:
      - `TortoiseGitAI-net472-vX.X.X.exe`: **(Recommended)** This is a single, self-contained executable built for .NET Framework 4.7.2. It should run on any modern Windows 10/11 system without needing to install anything extra.
      - `TortoiseGitAI-net9.0-windows-vX.X.X.zip`: This version uses the latest .NET 9.0. It requires the [.NET 9.0 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/9.0) to be installed on your machine.

2.  **A Note on Windows SmartScreen**:

    When you first run the downloaded executable, Windows Defender SmartScreen might show a warning. This can happen with new applications that haven't built a reputation yet. The releases are code-signed to ensure their integrity.

    **It is safe to run.** To proceed, click **"More info"** and then **"Run anyway"**.

2.  **Place it Somewhere Convenient**:
    - For the `net472` version, move the downloaded `.exe` file to a permanent location on your computer (e.g., `C:\Tools\`).
    - For the `net9.0` version, unzip the archive and move the contents to a permanent location.

## Configuration

### 1. Get a Gemini API Key

You need a free API key from Google to use the AI model.

- Go to [Google AI Studio](https://aistudio.google.com/).
- Click **"Get API key"** and follow the prompts to create a new key.
- Copy the generated API key.

### 2. Set the Environment Variable

The tool reads the API key from an environment variable.

- Press `Win + S` and search for "Edit the system environment variables".
- In the System Properties window, click the **"Environment Variables..."** button.
- In the "User variables" section, click **"New..."**.
  - **Variable name**: `TORTOISEGIT_GEMINI_API_KEY`
  - **Variable value**: Paste the API key you copied from Google AI Studio.
- Click **OK** on all windows to save the changes.
- **Important**: You may need to restart any open Command Prompt, PowerShell, or File Explorer windows for the new variable to be recognized.

## How to Use

1.  **Run the Tool**:

    - Double-click `TortoiseGitAI.exe` to start it.
    - A console window will appear, indicating that it is monitoring for the TortoiseGit commit dialog.

2.  **Make it Run on Startup (Recommended)**:

    - Create a shortcut to `TortoiseGitAI.exe`.
    - Press `Win + R`, type `shell:startup`, and press Enter. This opens your Startup folder.
    - Move the shortcut you created into the Startup folder.
    - Now, the tool will automatically start every time you log in to Windows.

3.  **Commit Your Changes with TortoiseGit**:
    - Right-click in your repository, go to the TortoiseGit menu, and click **"Commit..."**.
    - When the commit dialog opens, the tool will automatically:
      1. Detect the window.
      2. Display a "Generating..." message in the text box.
      3. Run `git diff` to get the changes.
      4. Send the diff to the Gemini API.
      5. Inject the AI-generated commit message into the text box.
    - Review the message, make any edits you like, and click **"Commit"**.

## How It Works

The application uses the Windows UI Automation framework to listen for new windows. When it finds a window belonging to the `TortoiseGitProc.exe` process with "Commit" in its title, it triggers the following workflow:

1.  **Identify Repository**: It determines the repository path from the commit dialog's process information.
2.  **Gather Context**: It runs `git status`, `git log`, and `git diff` to collect information about the pending changes and recent project history.
3.  **Call AI**: It sends this context to the Gemini API with a carefully crafted prompt asking for a conventional commit message.
4.  **Inject Message**: It uses UI Automation and Windows messages (`WM_PASTE`) to safely and efficiently place the generated message into the commit dialog's text box.
