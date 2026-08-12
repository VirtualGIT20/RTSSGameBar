#include <windows.h>
#include <shellapi.h>
#include <shlobj.h>
#include <tlhelp32.h>

#include <algorithm>
#include <cwctype>
#include <exception>
#include <iomanip>
#include <sstream>
#include <string>
#include <vector>

namespace
{
    constexpr int ExitSuccess = 0;
    constexpr int ExitRtssStillRunning = 20; // Reserved for the existing Helper/Setup exit-code contract.
    constexpr int ExitRtssNotFound = 21;
    constexpr int ExitBundledPluginMissing = 22;
    constexpr int ExitFileOperationFailed = 23;
    constexpr UINT WmClose = WM_CLOSE;

    struct FileOperationResult
    {
        bool success = false;
        DWORD error = ERROR_SUCCESS;
        std::wstring message;
    };

    std::wstring FormatWin32Error(DWORD error)
    {
        wchar_t buffer[1024] = {};
        const DWORD length = FormatMessageW(
            FORMAT_MESSAGE_FROM_SYSTEM | FORMAT_MESSAGE_IGNORE_INSERTS,
            nullptr,
            error,
            0,
            buffer,
            static_cast<DWORD>(_countof(buffer)),
            nullptr);

        std::wstring message = length == 0 ? L"unknown error" : std::wstring(buffer, length);
        while (!message.empty() && std::iswspace(message.back()) != 0)
            message.pop_back();

        return L"Win32 error " + std::to_wstring(error) + L": " + message;
    }

    std::string Utf8(const std::wstring& value)
    {
        if (value.empty())
            return {};

        const int bytes = WideCharToMultiByte(
            CP_UTF8,
            0,
            value.data(),
            static_cast<int>(value.size()),
            nullptr,
            0,
            nullptr,
            nullptr);
        if (bytes <= 0)
            return {};

        std::string result(static_cast<size_t>(bytes), '\0');
        const int converted = WideCharToMultiByte(
            CP_UTF8,
            0,
            value.data(),
            static_cast<int>(value.size()),
            result.data(),
            bytes,
            nullptr,
            nullptr);
        if (converted != bytes)
            return {};
        return result;
    }

    std::wstring NarrowToWide(const char* value)
    {
        if (value == nullptr || *value == '\0')
            return {};

        const int chars = MultiByteToWideChar(CP_ACP, 0, value, -1, nullptr, 0);
        if (chars <= 1)
            return {};

        std::vector<wchar_t> buffer(static_cast<size_t>(chars));
        if (MultiByteToWideChar(CP_ACP, 0, value, -1, buffer.data(), chars) == 0)
            return {};
        return std::wstring(buffer.data());
    }

    std::wstring GetEnvironmentVariableText(const wchar_t* name)
    {
        const DWORD needed = GetEnvironmentVariableW(name, nullptr, 0);
        if (needed == 0)
            return {};

        std::vector<wchar_t> buffer(static_cast<size_t>(needed));
        const DWORD length = GetEnvironmentVariableW(name, buffer.data(), needed);
        if (length == 0 || length >= needed)
            return {};
        return std::wstring(buffer.data(), length);
    }

    std::wstring CombinePath(const std::wstring& left, const std::wstring& right)
    {
        if (left.empty())
            return right;
        if (right.empty())
            return left;
        if (left.back() == L'\\' || left.back() == L'/')
            return left + right;
        return left + L"\\" + right;
    }

    bool DirectoryExists(const std::wstring& path)
    {
        const DWORD attributes = GetFileAttributesW(path.c_str());
        return attributes != INVALID_FILE_ATTRIBUTES && (attributes & FILE_ATTRIBUTE_DIRECTORY) != 0;
    }

    bool FileExists(const std::wstring& path)
    {
        const DWORD attributes = GetFileAttributesW(path.c_str());
        return attributes != INVALID_FILE_ATTRIBUTES && (attributes & FILE_ATTRIBUTE_DIRECTORY) == 0;
    }

    std::wstring TimestampNow()
    {
        FILETIME utcFileTime = {};
        GetSystemTimePreciseAsFileTime(&utcFileTime);

        FILETIME localFileTime = {};
        if (!FileTimeToLocalFileTime(&utcFileTime, &localFileTime))
            localFileTime = utcFileTime;

        SYSTEMTIME localTime = {};
        FileTimeToSystemTime(&localFileTime, &localTime);

        ULARGE_INTEGER localTicks = {};
        localTicks.LowPart = localFileTime.dwLowDateTime;
        localTicks.HighPart = localFileTime.dwHighDateTime;
        const unsigned long long fraction = localTicks.QuadPart % 10000000ULL;

        TIME_ZONE_INFORMATION zone = {};
        const DWORD zoneState = GetTimeZoneInformation(&zone);
        LONG bias = zone.Bias;
        if (zoneState == TIME_ZONE_ID_STANDARD)
            bias += zone.StandardBias;
        else if (zoneState == TIME_ZONE_ID_DAYLIGHT)
            bias += zone.DaylightBias;

        const LONG offsetMinutes = -bias;
        const wchar_t offsetSign = offsetMinutes < 0 ? L'-' : L'+';
        const LONG absoluteOffset = offsetMinutes < 0 ? -offsetMinutes : offsetMinutes;

        std::wostringstream text;
        text << std::setfill(L'0')
             << std::setw(4) << localTime.wYear << L'-'
             << std::setw(2) << localTime.wMonth << L'-'
             << std::setw(2) << localTime.wDay << L'T'
             << std::setw(2) << localTime.wHour << L':'
             << std::setw(2) << localTime.wMinute << L':'
             << std::setw(2) << localTime.wSecond << L'.'
             << std::setw(7) << fraction
             << offsetSign
             << std::setw(2) << (absoluteOffset / 60) << L':'
             << std::setw(2) << (absoluteOffset % 60);
        return text.str();
    }

    void EnsureDirectoryForLog(const std::wstring& path)
    {
        if (path.empty() || DirectoryExists(path))
            return;
        SHCreateDirectoryExW(nullptr, path.c_str(), nullptr);
    }

    void Log(const std::wstring& message)
    {
        const std::wstring localAppData = GetEnvironmentVariableText(L"LOCALAPPDATA");
        if (localAppData.empty())
            return;

        const std::wstring directory = CombinePath(localAppData, L"RTSSGameBar");
        EnsureDirectoryForLog(directory);
        const std::wstring path = CombinePath(directory, L"setup.log");

        HANDLE file = CreateFileW(
            path.c_str(),
            FILE_APPEND_DATA,
            FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
            nullptr,
            OPEN_ALWAYS,
            FILE_ATTRIBUTE_NORMAL,
            nullptr);
        if (file == INVALID_HANDLE_VALUE)
            return;

        const std::wstring line = TimestampNow() + L" [INFO] " + message + L"\r\n";
        const std::string utf8 = Utf8(line);
        if (!utf8.empty())
        {
            DWORD written = 0;
            WriteFile(file, utf8.data(), static_cast<DWORD>(utf8.size()), &written, nullptr);
        }
        CloseHandle(file);
    }

    bool IsElevated()
    {
        HANDLE token = nullptr;
        if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &token))
            return false;

        TOKEN_ELEVATION elevation = {};
        DWORD bytes = 0;
        const BOOL ok = GetTokenInformation(token, TokenElevation, &elevation, sizeof(elevation), &bytes);
        CloseHandle(token);
        return ok != FALSE && elevation.TokenIsElevated != 0;
    }

    std::wstring TrimAndLower(std::wstring value)
    {
        const auto notSpace = [](wchar_t ch) { return std::iswspace(ch) == 0; };
        const auto first = std::find_if(value.begin(), value.end(), notSpace);
        const auto last = std::find_if(value.rbegin(), value.rend(), notSpace).base();
        if (first >= last)
            return {};

        std::wstring result(first, last);
        std::transform(result.begin(), result.end(), result.begin(), [](wchar_t ch) {
            return static_cast<wchar_t>(std::towlower(ch));
        });
        return result;
    }

    std::wstring GetAction()
    {
        int argc = 0;
        wchar_t** argv = CommandLineToArgvW(GetCommandLineW(), &argc);
        if (argv == nullptr)
            return {};

        std::wstring action;
        if (argc > 1)
            action = TrimAndLower(argv[1]);
        LocalFree(argv);
        return action;
    }

    std::wstring GetExecutablePath()
    {
        std::vector<wchar_t> buffer(32768);
        const DWORD length = GetModuleFileNameW(nullptr, buffer.data(), static_cast<DWORD>(buffer.size()));
        if (length == 0 || length >= buffer.size())
            return {};
        return std::wstring(buffer.data(), length);
    }

    std::wstring GetExecutableDirectory()
    {
        const std::wstring path = GetExecutablePath();
        const size_t separator = path.find_last_of(L"\\/");
        return separator == std::wstring::npos ? std::wstring() : path.substr(0, separator);
    }

    std::wstring ExpandRegistryString(const std::wstring& value)
    {
        const DWORD needed = ExpandEnvironmentStringsW(value.c_str(), nullptr, 0);
        if (needed == 0)
            return value;

        std::vector<wchar_t> buffer(static_cast<size_t>(needed));
        const DWORD length = ExpandEnvironmentStringsW(value.c_str(), buffer.data(), needed);
        if (length == 0 || length > needed)
            return value;
        return std::wstring(buffer.data());
    }

    std::wstring ReadInstallDir(REGSAM view)
    {
        HKEY key = nullptr;
        const LSTATUS openStatus = RegOpenKeyExW(
            HKEY_LOCAL_MACHINE,
            L"SOFTWARE\\Unwinder\\RTSS",
            0,
            KEY_QUERY_VALUE | view,
            &key);
        if (openStatus != ERROR_SUCCESS)
            return {};

        DWORD type = 0;
        DWORD bytes = 0;
        LSTATUS status = RegQueryValueExW(key, L"InstallDir", nullptr, &type, nullptr, &bytes);
        if (status != ERROR_SUCCESS || (type != REG_SZ && type != REG_EXPAND_SZ) || bytes < sizeof(wchar_t))
        {
            RegCloseKey(key);
            return {};
        }

        std::vector<wchar_t> buffer(static_cast<size_t>(bytes / sizeof(wchar_t)) + 1U, L'\0');
        status = RegQueryValueExW(
            key,
            L"InstallDir",
            nullptr,
            &type,
            reinterpret_cast<LPBYTE>(buffer.data()),
            &bytes);
        RegCloseKey(key);
        if (status != ERROR_SUCCESS)
            return {};

        std::wstring result(buffer.data());
        if (type == REG_EXPAND_SZ)
            result = ExpandRegistryString(result);
        while (!result.empty() && result.back() == L'\\')
            result.pop_back();
        return result;
    }

    std::wstring LocateRtss()
    {
        std::wstring directory = ReadInstallDir(KEY_WOW64_32KEY);
        if (directory.empty())
            directory = ReadInstallDir(KEY_WOW64_64KEY);
        if (!directory.empty())
            return directory;

        const std::wstring programFilesX86 = GetEnvironmentVariableText(L"ProgramFiles(x86)");
        return CombinePath(programFilesX86, L"RivaTuner Statistics Server");
    }

    std::vector<DWORD> GetRtssProcesses()
    {
        std::vector<DWORD> result;
        HANDLE snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snapshot == INVALID_HANDLE_VALUE)
        {
            Log(L"Could not enumerate RTSS processes: " + FormatWin32Error(GetLastError()));
            return result;
        }

        PROCESSENTRY32W entry = {};
        entry.dwSize = sizeof(entry);
        if (!Process32FirstW(snapshot, &entry))
        {
            const DWORD error = GetLastError();
            CloseHandle(snapshot);
            if (error != ERROR_NO_MORE_FILES)
                Log(L"Could not enumerate RTSS processes: " + FormatWin32Error(error));
            return result;
        }

        do
        {
            if (_wcsicmp(entry.szExeFile, L"RTSS.exe") == 0)
                result.push_back(entry.th32ProcessID);
        } while (Process32NextW(snapshot, &entry));

        CloseHandle(snapshot);
        return result;
    }

    bool IsRtssRunning()
    {
        return !GetRtssProcesses().empty();
    }

    BOOL CALLBACK CloseRtssWindowProc(HWND window, LPARAM parameter)
    {
        DWORD owner = 0;
        GetWindowThreadProcessId(window, &owner);
        if (owner == static_cast<DWORD>(parameter))
            PostMessageW(window, WmClose, 0, 0);
        return TRUE;
    }

    void RequestRtssWindowClose(DWORD processId)
    {
        if (!EnumWindows(CloseRtssWindowProc, static_cast<LPARAM>(processId)))
            Log(L"RTSS WM_CLOSE enumeration failed for pid=" + std::to_wstring(processId) + L": " + FormatWin32Error(GetLastError()));
    }

    void EnsureRtssStoppedForMaintenance()
    {
        std::vector<DWORD> processes = GetRtssProcesses();
        if (processes.empty())
            return;

        Log(L"RTSS is running inside the elevated maintenance phase; requesting close for "
            + std::to_wstring(processes.size()) + L" process(es).");
        for (const DWORD processId : processes)
            RequestRtssWindowClose(processId);

        const ULONGLONG gracefulDeadline = GetTickCount64() + 650ULL;
        while (GetTickCount64() < gracefulDeadline)
        {
            if (!IsRtssRunning())
                return;
            Sleep(50);
        }

        processes = GetRtssProcesses();
        for (const DWORD processId : processes)
        {
            Log(L"RTSS remained/rerespawned during maintenance; terminating pid=" + std::to_wstring(processId) + L".");
            HANDLE process = OpenProcess(PROCESS_TERMINATE | SYNCHRONIZE, FALSE, processId);
            if (process == nullptr)
            {
                Log(L"Could not terminate RTSS pid=" + std::to_wstring(processId) + L": " + FormatWin32Error(GetLastError()));
                continue;
            }

            if (!TerminateProcess(process, 1))
                Log(L"Could not terminate RTSS pid=" + std::to_wstring(processId) + L": " + FormatWin32Error(GetLastError()));
            else
                WaitForSingleObject(process, 900);
            CloseHandle(process);
        }

        Sleep(35);
    }

    FileOperationResult EnsureDirectoryExists(const std::wstring& directory)
    {
        if (DirectoryExists(directory))
            return { true, ERROR_SUCCESS, {} };

        const int result = SHCreateDirectoryExW(nullptr, directory.c_str(), nullptr);
        if (result == ERROR_SUCCESS || result == ERROR_FILE_EXISTS || result == ERROR_ALREADY_EXISTS || DirectoryExists(directory))
            return { true, ERROR_SUCCESS, {} };

        return { false, static_cast<DWORD>(result), FormatWin32Error(static_cast<DWORD>(result)) };
    }

    FileOperationResult PerformFileOperation(
        const std::wstring& action,
        const std::wstring& source,
        const std::wstring& target,
        const std::wstring& targetDirectory)
    {
        FileOperationResult directoryResult = EnsureDirectoryExists(targetDirectory);
        if (!directoryResult.success)
            return directoryResult;

        if (action == L"remove")
        {
            if (FileExists(target) && !DeleteFileW(target.c_str()))
            {
                const DWORD error = GetLastError();
                return { false, error, FormatWin32Error(error) };
            }

            if (FileExists(target))
                return { false, ERROR_SHARING_VIOLATION, L"The integration plugin still exists after delete." };

            Log(L"Removed integration plugin: " + target);
            return { true, ERROR_SUCCESS, {} };
        }

        if (!CopyFileW(source.c_str(), target.c_str(), FALSE))
        {
            const DWORD error = GetLastError();
            return { false, error, FormatWin32Error(error) };
        }

        Log(L"Copied integration plugin: " + source + L" -> " + target);
        return { true, ERROR_SUCCESS, {} };
    }

    int Run()
    {
        const std::wstring action = GetAction();
        Log(L"Setup started. action=" + action + L" elevated=" + (IsElevated() ? L"True" : L"False"));

        if (action != L"install" && action != L"update" && action != L"remove")
        {
            Log(L"Unsupported action: " + action);
            return ExitFileOperationFailed;
        }

        const std::wstring rtssDirectory = LocateRtss();
        if (rtssDirectory.empty() || !FileExists(CombinePath(rtssDirectory, L"RTSS.exe")))
        {
            Log(L"RTSS installation not found.");
            return ExitRtssNotFound;
        }

        const std::wstring targetDirectory = CombinePath(CombinePath(rtssDirectory, L"Plugins"), L"Client");
        const std::wstring target = CombinePath(targetDirectory, L"RTSSGameBarPlugin.dll");
        const std::wstring source = CombinePath(GetExecutableDirectory(), L"RTSSGameBarPlugin.dll");
        if (action != L"remove" && !FileExists(source))
        {
            Log(L"Bundled plugin missing: " + source);
            return ExitBundledPluginMissing;
        }

        const ULONGLONG deadline = GetTickCount64() + 8000ULL;
        std::wstring lastFileError = L"unknown error";
        unsigned int attempt = 0;

        while (GetTickCount64() < deadline)
        {
            ++attempt;

            // The normal helper closes RTSS before UAC, but MSI Afterburner can respawn RTSS
            // while the consent UI is on screen. Re-check in the elevated phase, close it
            // gracefully first, and force only the raced/respawned instance if necessary.
            EnsureRtssStoppedForMaintenance();

            const FileOperationResult result = PerformFileOperation(action, source, target, targetDirectory);
            if (result.success)
            {
                Log(L"Setup action completed successfully. RTSS restart is delegated to the normal helper.");
                return ExitSuccess;
            }

            lastFileError = result.message;
            if (result.error == ERROR_ACCESS_DENIED)
            {
                Log(L"File operation attempt " + std::to_wstring(attempt) + L" was denied: " + result.message);
            }
            else
            {
                Log(L"File operation attempt " + std::to_wstring(attempt)
                    + L" raced RTSS/another process: " + result.message);
            }

            Sleep(90);
        }

        Log(L"File operation failed after retries: " + lastFileError);
        return ExitFileOperationFailed;
    }
}

int WINAPI wWinMain(
    _In_ HINSTANCE hInstance,
    _In_opt_ HINSTANCE hPrevInstance,
    _In_ LPWSTR lpCmdLine,
    _In_ int nCmdShow)
{
    UNREFERENCED_PARAMETER(hInstance);
    UNREFERENCED_PARAMETER(hPrevInstance);
    UNREFERENCED_PARAMETER(lpCmdLine);
    UNREFERENCED_PARAMETER(nCmdShow);
    // Keep the numeric contract synchronized with IntegrationManager. ExitRtssStillRunning is
    // intentionally reserved even though current maintenance logic retries/forces only raced RTSS.
    static_assert(ExitRtssStillRunning == 20, "Helper/Setup exit-code contract changed");

    int exitCode = ExitFileOperationFailed;
    try
    {
        exitCode = Run();
    }
    catch (const std::exception& ex)
    {
        Log(L"Unhandled setup error: " + NarrowToWide(ex.what()));
        exitCode = ExitFileOperationFailed;
    }
    catch (...)
    {
        Log(L"Unhandled setup error: unknown native exception.");
        exitCode = ExitFileOperationFailed;
    }

    // Keep the one-shot setup teardown deterministic. The previous managed implementation used
    // the same direct termination strategy after successful work to avoid third-party/appcompat
    // DLL_PROCESS_DETACH failures observed in WER reports.
    Log(L"Setup work finished. terminating process directly with exit code " + std::to_wstring(exitCode) + L".");
    if (!TerminateProcess(GetCurrentProcess(), static_cast<UINT>(exitCode)))
    {
        Log(L"TerminateProcess failed with Win32 error " + std::to_wstring(GetLastError()) + L"; falling back to normal return.");
    }
    return exitCode;
}
