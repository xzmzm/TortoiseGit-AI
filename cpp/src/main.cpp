#define _CRT_SECURE_NO_WARNINGS
#define WIN32_LEAN_AND_MEAN

#include <winsock2.h> // Must be included before windows.h
#include <windows.h>  // For WinAPI
#include <Wbemidl.h>
#include <UIAutomation.h>
#include <comdef.h>
#include <atlbase.h>
#include <iostream>
#include <string>
#include <vector>
#include <sstream>
#include <regex>
#include <thread>
#include <future>
#include <chrono>
#include <memory>
#include <functional>

#include "httplib.h"
#include "json.hpp"

#pragma comment(lib, "wbemuuid.lib")
#pragma comment(lib, "ole32.lib")
#pragma comment(lib, "oleaut32.lib")
#pragma comment(lib, "user32.lib")
#pragma comment(lib, "ws2_32.lib")

using json = nlohmann::json;

// --- Helper Functions ---

std::wstring ToWide(const std::string &narrow)
{
    int size_needed = MultiByteToWideChar(CP_UTF8, 0, &narrow[0], (int)narrow.size(), NULL, 0);
    std::wstring wstrTo(size_needed, 0);
    MultiByteToWideChar(CP_UTF8, 0, &narrow[0], (int)narrow.size(), &wstrTo[0], size_needed);
    return wstrTo;
}

std::string ToNarrow(const std::wstring &wide)
{
    if (wide.empty())
        return std::string();
    int size_needed = WideCharToMultiByte(CP_UTF8, 0, &wide[0], (int)wide.size(), NULL, 0, NULL, NULL);
    std::string strTo(size_needed, 0);
    WideCharToMultiByte(CP_UTF8, 0, &wide[0], (int)wide.size(), &strTo[0], size_needed, NULL, NULL);
    return strTo;
}

struct ProcessOutput
{
    std::string output;
    std::string error;
    DWORD exitCode;
};

ProcessOutput RunCommand(const std::string &command, const std::string &args, const std::string &workingDir)
{
    ProcessOutput result;
    SECURITY_ATTRIBUTES sa;
    sa.nLength = sizeof(SECURITY_ATTRIBUTES);
    sa.bInheritHandle = TRUE;
    sa.lpSecurityDescriptor = NULL;

    HANDLE hReadOut, hWriteOut, hReadErr, hWriteErr;
    CreatePipe(&hReadOut, &hWriteOut, &sa, 0);
    CreatePipe(&hReadErr, &hWriteErr, &sa, 0);
    SetHandleInformation(hReadOut, HANDLE_FLAG_INHERIT, 0);
    SetHandleInformation(hReadErr, HANDLE_FLAG_INHERIT, 0);

    STARTUPINFOA si;
    PROCESS_INFORMATION pi;
    ZeroMemory(&si, sizeof(si));
    si.cb = sizeof(si);
    si.hStdError = hWriteErr;
    si.hStdOutput = hWriteOut;
    si.dwFlags |= STARTF_USESTDHANDLES;

    ZeroMemory(&pi, sizeof(pi));

    std::string fullCommand = command + " " + args;

    if (!CreateProcessA(NULL, &fullCommand[0], NULL, NULL, TRUE, CREATE_NO_WINDOW, NULL, workingDir.empty() ? NULL : workingDir.c_str(), &si, &pi))
    {
        result.error = "CreateProcess failed.";
        result.exitCode = -1;
        return result;
    }

    CloseHandle(hWriteOut);
    CloseHandle(hWriteErr);

    auto read_pipe = [](HANDLE hPipe, std::string &out_str)
    {
        CHAR buffer[4096];
        DWORD dwRead;
        while (ReadFile(hPipe, buffer, sizeof(buffer) - 1, &dwRead, NULL) && dwRead != 0)
        {
            buffer[dwRead] = '\0';
            out_str += buffer;
        }
    };

    std::thread out_thread(read_pipe, hReadOut, std::ref(result.output));
    std::thread err_thread(read_pipe, hReadErr, std::ref(result.error));

    WaitForSingleObject(pi.hProcess, INFINITE);
    GetExitCodeProcess(pi.hProcess, &result.exitCode);

    out_thread.join();
    err_thread.join();

    CloseHandle(hReadOut);
    CloseHandle(hReadErr);
    CloseHandle(pi.hProcess);
    CloseHandle(pi.hThread);

    return result;
}

// --- Git & Repo Helpers ---

namespace GitDiffHelper
{
    std::string GetDiff(const std::string &workingDirectory)
    {
        try
        {
            auto status = RunCommand("git", "status --short .", workingDirectory);
            if (status.exitCode != 0)
            {
                std::cerr << "Warning: 'git status' command failed with exit code " << status.exitCode << ":\n"
                          << status.error << std::endl;
            }

            if (status.output.find_first_not_of(" \t\r\n") == std::string::npos)
            {
                return "";
            }

            std::stringstream fullContext;

            auto log = RunCommand("git", "log -n 5 --oneline --no-decorate", workingDirectory);
            if (log.exitCode == 0 && !log.output.empty())
            {
                fullContext << "Recent commits:\n"
                            << log.output << "\n\n";
            }
            else if (log.exitCode != 0)
            {
                std::cerr << "Warning: 'git log' failed with exit code " << log.exitCode << ":\n"
                          << log.error << std::endl;
            }

            fullContext << "Changed files:\n"
                        << status.output << "\n\n";
            fullContext << "Full diff:\n";

            const int MaxDiffBytes = 30 * 1024;

            auto diff = RunCommand("git", "diff HEAD .", workingDirectory);
            if (diff.exitCode != 0)
                return "Error: 'git diff HEAD' failed.\n" + diff.error;

            auto cachedDiff = RunCommand("git", "diff --cached HEAD .", workingDirectory);
            if (cachedDiff.exitCode != 0)
                return "Error: 'git diff --cached HEAD' failed.\n" + cachedDiff.error;

            std::string combinedDiff = diff.output + "\n" + cachedDiff.output;

            if (combinedDiff.length() > MaxDiffBytes)
            {
                std::cerr << "Warning: Total diff is large. Truncating." << std::endl;
                // Simple truncation for C++ version
                combinedDiff = combinedDiff.substr(0, MaxDiffBytes) + "\n[... diff truncated ...]";
            }

            fullContext << combinedDiff;
            return fullContext.str();
        }
        catch (const std::exception &e)
        {
            return "Error: An unexpected exception occurred while gathering git context. " + std::string(e.what());
        }
    }
}

namespace RepoFinder
{
    std::string GetPathFromProcess(DWORD processId)
    {
        CComPtr<IWbemLocator> pLoc;
        HRESULT hres = CoCreateInstance(CLSID_WbemLocator, 0, CLSCTX_INPROC_SERVER, IID_IWbemLocator, (LPVOID *)&pLoc);
        if (FAILED(hres))
            return "";

        CComPtr<IWbemServices> pSvc;
        hres = pLoc->ConnectServer(_bstr_t(L"ROOT\\CIMV2"), NULL, NULL, 0, NULL, 0, 0, &pSvc);
        if (FAILED(hres))
            return "";

        hres = CoSetProxyBlanket(pSvc, RPC_C_AUTHN_WINNT, RPC_C_AUTHZ_NONE, NULL, RPC_C_AUTHN_LEVEL_CALL, RPC_C_IMP_LEVEL_IMPERSONATE, NULL, EOAC_NONE);
        if (FAILED(hres))
            return "";

        CComPtr<IEnumWbemClassObject> pEnumerator;
        std::wstring query = L"SELECT CommandLine FROM Win32_Process WHERE ProcessId = " + std::to_wstring(processId);
        hres = pSvc->ExecQuery(_bstr_t(L"WQL"), _bstr_t(query.c_str()), WBEM_FLAG_FORWARD_ONLY | WBEM_FLAG_RETURN_IMMEDIATELY, NULL, &pEnumerator);
        if (FAILED(hres))
            return "";

        CComPtr<IWbemClassObject> pclsObj;
        ULONG uReturn = 0;
        std::string commandLine;
        if (pEnumerator && pEnumerator->Next(WBEM_INFINITE, 1, &pclsObj, &uReturn) == S_OK && uReturn != 0)
        {
            VARIANT vtProp;
            if (SUCCEEDED(pclsObj->Get(L"CommandLine", 0, &vtProp, 0, 0)) && vtProp.vt == VT_BSTR)
            {
                commandLine = ToNarrow(vtProp.bstrVal);
            }
            VariantClear(&vtProp);
        }

        if (commandLine.empty())
            return "";

        // Re-written to avoid C++11 raw string literals (R"()") for better compiler compatibility.
        std::smatch match;
        std::regex re("/path:\"([^\"]+)\"");
        if (std::regex_search(commandLine, match, re) && match.size() > 1)
        {
            return match[1].str();
        }
        return "";
    }

    std::string FindRepoRootFromDialog(CComPtr<IUIAutomationElement> commitDialog)
    {
        try
        {
            int processId;
            commitDialog->get_CurrentProcessId(&processId);
            std::string startingPath = GetPathFromProcess(processId);

            if (startingPath.empty())
            {
                std::cout << "Could not find path from command line, trying window title as fallback." << std::endl;
                BSTR nameBSTR;
                commitDialog->get_CurrentName(&nameBSTR);
                std::string title = ToNarrow(nameBSTR);
                SysFreeString(nameBSTR);

                size_t pos = title.find(" - Commit");
                if (pos != std::string::npos)
                {
                    startingPath = title.substr(0, pos);
                }
            }

            if (startingPath.empty())
            {
                std::cout << "Could not determine a starting path." << std::endl;
                return "";
            }
            std::cout << "Found starting path: " << startingPath << std::endl;
            return startingPath;
        }
        catch (const std::exception &e)
        {
            std::cerr << "Error finding repository root: " << e.what() << std::endl;
            return "";
        }
    }
}

// --- Gemini API Client ---

namespace GeminiApiClient
{
    std::string GenerateCommitMessage(const std::string &prompt)
    {
        const char *apiKey = std::getenv("TORTOISEGIT_GEMINI_API_KEY");
        if (!apiKey)
        {
            return "Error: TORTOISEGIT_GEMINI_API_KEY environment variable not set.";
        }

        json requestBody;
        requestBody["contents"][0]["parts"][0]["text"] = prompt;

        httplib::Client cli("https://generativelanguage.googleapis.com");

        std::string path = "/v1beta/models/gemini-flash-lite-latest:generateContent?key=" + std::string(apiKey);

        try
        {
            if (auto res = cli.Post(path.c_str(), requestBody.dump(), "application/json"))
            {
                if (res->status == 200)
                {
                    json responseBody = json::parse(res->body);
                    if (responseBody.contains("candidates") && responseBody["candidates"].is_array() && !responseBody["candidates"].empty())
                    {
                        if (responseBody["candidates"][0].contains("content") && responseBody["candidates"][0]["content"].contains("parts") && responseBody["candidates"][0]["content"]["parts"].is_array() && !responseBody["candidates"][0]["content"]["parts"].empty())
                        {
                            return responseBody["candidates"][0]["content"]["parts"][0]["text"];
                        }
                    }
                    return "Error: Could not parse a valid response from the API.";
                }
                else
                {
                    return "Error: API call failed with status " + std::to_string(res->status) + ". Details: " + res->body;
                }
            }
            else
            {
                auto err = res.error();
                return "Error: HTTP request failed: " + httplib::to_string(err);
            }
        }
        catch (const std::exception &e)
        {
            return "Error: An exception occurred while calling the API. " + std::string(e.what());
        }
    }
}

// --- TortoiseGit UI Interaction ---

class TortoiseGitInjector
{
public:
    bool TryFindMessageBox(CComPtr<IUIAutomationElement> commitDialog, CComPtr<IUIAutomationElement> &messageBox)
    {
        if (!commitDialog)
            return false;

        CComPtr<IUIAutomationCondition> condition;
        CComVariant propVal(L"Scintilla");
        this->pAutomation->CreatePropertyCondition(UIA_ClassNamePropertyId, propVal, &condition);

        if (condition)
        {
            commitDialog->FindFirst(TreeScope_Descendants, condition, &messageBox);
        }

        return messageBox != nullptr;
    }

    bool SetCommitMessage(CComPtr<IUIAutomationElement> messageBox, const std::string &text)
    {
        if (!messageBox)
            return false;

        HWND hwnd = 0;
        HRESULT hr = messageBox->get_CurrentNativeWindowHandle((UIA_HWND *)&hwnd);
        if (FAILED(hr) || !IsWindow(hwnd))
        {
            std::cerr << "Error: UI element disappeared or handle is invalid." << std::endl;
            return false;
        }

        messageBox->SetFocus();

        // The clipboard operations must happen on an STA thread.
        // The event handler is already STA, so we can do this directly.
        bool success = false;
        if (OpenClipboard(NULL))
        {
            EmptyClipboard();
            std::wstring wtext = ToWide(text);
            HGLOBAL hg = GlobalAlloc(GMEM_MOVEABLE, (wtext.size() + 1) * sizeof(wchar_t));
            if (hg)
            {
                memcpy(GlobalLock(hg), wtext.c_str(), (wtext.size() + 1) * sizeof(wchar_t));
                GlobalUnlock(hg);
                SetClipboardData(CF_UNICODETEXT, hg);
                success = true;
            }
            CloseClipboard();
        }

        if (success)
        {
            const UINT SciSelectAll = 2013;
            SendMessage(hwnd, SciSelectAll, 0, 0); // Select all
            SendMessage(hwnd, WM_PASTE, 0, 0);     // Paste
        }

        return success;
    }

    TortoiseGitInjector(CComPtr<IUIAutomation> automation) : pAutomation(automation) {}

private:
    CComPtr<IUIAutomation> pAutomation;
};

// --- Main Event Watcher ---

class CommitDialogHandler : public IUIAutomationEventHandler
{
public:
    CommitDialogHandler(CComPtr<IUIAutomation> pAutomation)
        : refCount(1), pAutomation(pAutomation), injector(pAutomation) {}

    // IUnknown methods
    ULONG STDMETHODCALLTYPE AddRef() override { return InterlockedIncrement(&this->refCount); }
    ULONG STDMETHODCALLTYPE Release() override
    {
        ULONG count = InterlockedDecrement(&this->refCount);
        if (count == 0)
        {
            delete this;
            return 0;
        }
        return count;
    }
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID riid, void **ppvObject) override
    {
        if (riid == __uuidof(IUnknown) || riid == __uuidof(IUIAutomationEventHandler))
        {
            *ppvObject = this;
            this->AddRef();
            return S_OK;
        }
        *ppvObject = NULL;
        return E_NOINTERFACE;
    }

    // IUIAutomationEventHandler method
    HRESULT STDMETHODCALLTYPE HandleAutomationEvent(IUIAutomationElement *pSender, EVENTID eventId) override
    {
        if (eventId != UIA_Window_WindowOpenedEventId)
            return S_OK;

        CComPtr<IUIAutomationElement> openedWindow = pSender;
        if (!openedWindow)
            return S_OK;

        BSTR windowNameBSTR;
        openedWindow->get_CurrentName(&windowNameBSTR);
        std::wstring windowName = windowNameBSTR ? windowNameBSTR : L"";
        SysFreeString(windowNameBSTR);

        // Heuristic to identify the TortoiseGit commit dialog.
        // Checking process name is more robust but also more complex. This is usually sufficient.
        if (windowName.find(L"Commit") == std::wstring::npos || windowName.find(L"TortoiseGit") == std::wstring::npos)
        {
            return S_OK;
        }

        std::wcout << L"\n--- Potential Commit Dialog Found: " << windowName << L" ---" << std::endl;

        CComPtr<IUIAutomationElement> messageBox;
        if (this->injector.TryFindMessageBox(openedWindow, messageBox) && messageBox)
        {
            // To avoid blocking the event handler, process this in a new thread.
            std::thread(&CommitDialogHandler::ProcessCommitDialog, this, openedWindow, messageBox).detach();
        }

        return S_OK;
    }

private:
    void ProcessCommitDialog(CComPtr<IUIAutomationElement> commitDialog, CComPtr<IUIAutomationElement> messageBox)
    {

        // COM needs to be initialized for each new thread that uses it.
        CoInitialize(NULL);

        this->injector.SetCommitMessage(messageBox, "Generating commit message... [Detecting repository]");

        std::string repoRoot = RepoFinder::FindRepoRootFromDialog(commitDialog);
        if (repoRoot.empty())
        {
            this->injector.SetCommitMessage(messageBox, "Error: Could not determine Git repository root.");
            CoUninitialize();
            return;
        }

        this->injector.SetCommitMessage(messageBox, "Generating commit message... [Repo: " + repoRoot.substr(repoRoot.find_last_of("/\\") + 1) + "]");
        std::string gitContext = GitDiffHelper::GetDiff(repoRoot);

        if (gitContext.rfind("Error:", 0) == 0)
        { // starts_with in C++20
            this->injector.SetCommitMessage(messageBox, gitContext);
            CoUninitialize();
            return;
        }

        if (gitContext.find_first_not_of(" \t\r\n") == std::string::npos)
        {
            this->injector.SetCommitMessage(messageBox, "No changes detected to commit.");
            std::cout << "No changes detected. Aborting AI generation." << std::endl;
            CoUninitialize();
            return;
        }

        // Re-written to avoid C++11 raw string literals (R"()") for better compiler compatibility.
        // This prevents "C2001: newline in constant" errors with older toolsets.
        std::string prompt = "You are an expert git commit message generation tool. Based on the following git context "
                             "(recent commits, changed files, and full diff), create a concise, conventional commit message. "
                             "The message must have a short subject line (under 50 characters), a blank line, and then a brief "
                             "bulleted description of the most important changes. Your response MUST be only the raw commit message text. "
                             "DO NOT include explanations, options, markdown formatting, or placeholders like '[Your ID]'.\n\n"
                             "Git Context:\n```\n" +
                             gitContext + "\n```";

        std::cout << "--- Sending Request to Gemini ---" << std::endl;
        std::cout << "[Git Context Length]: " << gitContext.length() << " characters" << std::endl;
        std::cout << "---------------------------------" << std::endl;

        // The Gemini API call is run on a separate thread using std::async to keep the UI updater running.
        // With OpenSSL, we don't need to worry about COM apartment state for networking calls.
        auto future = std::async(std::launch::async, [&prompt]() -> std::string
                                 {
            std::string result = GeminiApiClient::GenerateCommitMessage(prompt);
            return result; });

        std::string initialMessage = "Generating AI commit message...";
        this->injector.SetCommitMessage(messageBox, initialMessage);

        int elapsedSeconds = 0;
        while (future.wait_for(std::chrono::seconds(1)) != std::future_status::ready)
        {
            elapsedSeconds++;

            UIA_HWND hwnd;
            if (FAILED(messageBox->get_CurrentNativeWindowHandle(&hwnd)) || !IsWindow(static_cast<HWND>(hwnd)))
            {
                std::cout << "Commit dialog closed while generating message. Aborting." << std::endl;
                CoUninitialize();
                return;
            }
            this->injector.SetCommitMessage(messageBox, initialMessage + " (" + std::to_string(elapsedSeconds) + "s)");
        }

        std::string finalMessage = future.get();

        UIA_HWND hwnd;
        if (FAILED(messageBox->get_CurrentNativeWindowHandle(&hwnd)) || !IsWindow(static_cast<HWND>(hwnd)))
        {
            std::cout << "Commit dialog closed before setting final message." << std::endl;
            CoUninitialize();
            return;
        }
        this->injector.SetCommitMessage(messageBox, finalMessage);

        std::cout << "Message set. Waiting for next event..." << std::endl;
        CoUninitialize();
    }

private:
    LONG refCount;
    CComPtr<IUIAutomation> pAutomation;
    TortoiseGitInjector injector;
};

// --- Main Program ---

int WINAPI WinMain(HINSTANCE hInstance, HINSTANCE hPrevInstance, LPSTR lpCmdLine, int nCmdShow)
{
    // For debugging, attach a console. Can be removed for release.
    // AllocConsole();
    // FILE* f;
    // freopen_s(&f, "CONOUT$", "w", stdout);
    // freopen_s(&f, "CONOUT$", "w", stderr);
    // freopen_s(&f, "CONIN$", "r", stdin);

    WSADATA wsaData;
    if (WSAStartup(MAKEWORD(2, 2), &wsaData) != 0)
    {
        MessageBox(NULL, "WSAStartup failed.", "Network Error", MB_OK | MB_ICONERROR);
        return 1;
    }

    HRESULT hr = CoInitializeEx(NULL, COINIT_APARTMENTTHREADED);
    if (FAILED(hr))
    {
        MessageBox(NULL, "Failed to initialize COM library.", "Error", MB_OK | MB_ICONERROR);
        return 1;
    }

    CComPtr<IUIAutomation> pAutomation;
    hr = CoCreateInstance(__uuidof(CUIAutomation), NULL, CLSCTX_INPROC_SERVER, __uuidof(IUIAutomation), (void **)&pAutomation);
    if (FAILED(hr) || !pAutomation)
    {
        MessageBox(NULL, "Failed to create UI Automation instance.", "Error", MB_OK | MB_ICONERROR);
        CoUninitialize();
        return 1;
    }

    std::cout << "Starting event-driven monitoring for TortoiseGit commit dialog..." << std::endl;

    CommitDialogHandler *pHandler = new CommitDialogHandler(pAutomation);
    if (!pHandler)
    {
        MessageBox(NULL, "Failed to create event handler.", "Error", MB_OK | MB_ICONERROR);
        CoUninitialize();
        return 1;
    }

    CComPtr<IUIAutomationElement> pRoot;
    pAutomation->GetRootElement(&pRoot);

    hr = pAutomation->AddAutomationEventHandler(
        UIA_Window_WindowOpenedEventId,
        pRoot,
        TreeScope_Children,
        NULL, // No cache request
        pHandler);

    if (FAILED(hr))
    {
        MessageBox(NULL, "Failed to add window opened event handler.", "Error", MB_OK | MB_ICONERROR);
        pHandler->Release();
        CoUninitialize();
        return 1;
    }

    std::cout << "Monitoring is active. This window can be hidden." << std::endl;

    // Standard message loop for a GUI application
    MSG msg;
    while (GetMessage(&msg, NULL, 0, 0) > 0)
    {
        TranslateMessage(&msg);
        DispatchMessage(&msg);
    }

    pAutomation->RemoveAutomationEventHandler(
        UIA_Window_WindowOpenedEventId,
        pRoot,
        pHandler);

    pHandler->Release();
    CoUninitialize();
    WSACleanup();

    return (int)msg.wParam;
}

// --- Entry point for console app for easier debugging ---
int main(int argc, char **argv)
{
    return WinMain(GetModuleHandle(NULL), NULL, GetCommandLineA(), SW_SHOWNORMAL);
}