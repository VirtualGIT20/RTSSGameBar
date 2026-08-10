#include "RTSSGameBarPlugin.h"

#include <Aclapi.h>
#include <Sddl.h>
#include <cstdio>
#include <cstdlib>
#include <cstring>

namespace
{
    const char* kPipePath = "\\\\.\\pipe\\RTSSGameBar.RTSSPlugin.v6";
    const char* kLogName = "RTSSGameBar-RTSSPlugin.log";
    const char* kPluginVersion = "1.0.0";
    const char* kCapabilities = "state,frameLimit,limiterType,limiterEnabled,osdVisible,osdZoom,osdPosition,closeRtss";
    const DWORD kFlagOsdVisible = 1u;
    const DWORD kFlagLimiterDisabled = 4u;

    HANDLE g_stopEvent = nullptr;
    HANDLE g_serverThread = nullptr;
    SRWLOCK g_rtssApiLock = SRWLOCK_INIT;

    typedef void (*LoadProfileProc)(LPCSTR);
    typedef void (*SaveProfileProc)(LPCSTR);
    typedef BOOL (*GetProfilePropertyProc)(LPCSTR, LPBYTE, DWORD);
    typedef BOOL (*SetProfilePropertyProc)(LPCSTR, LPBYTE, DWORD);
    typedef void (*UpdateProfilesProc)();
    typedef DWORD (*GetFlagsProc)();
    typedef DWORD (*SetFlagsProc)(DWORD, DWORD);

    struct RtssApi
    {
        LoadProfileProc loadProfile = nullptr;
        SaveProfileProc saveProfile = nullptr;
        GetProfilePropertyProc getProperty = nullptr;
        SetProfilePropertyProc setProperty = nullptr;
        UpdateProfilesProc updateProfiles = nullptr;
        GetFlagsProc getFlags = nullptr;
        SetFlagsProc setFlags = nullptr;
    };

    class ApiLock
    {
    public:
        ApiLock() { AcquireSRWLockExclusive(&g_rtssApiLock); }
        ~ApiLock() { ReleaseSRWLockExclusive(&g_rtssApiLock); }
    };

    void LogLine(const char* message)
    {
        char tempPath[MAX_PATH] = {};
        if (!GetTempPathA(MAX_PATH, tempPath))
            return;

        char logPath[MAX_PATH] = {};
        if (sprintf_s(logPath, "%s%s", tempPath, kLogName) <= 0)
            return;

        HANDLE file = CreateFileA(logPath, FILE_APPEND_DATA,
            FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
            nullptr, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
        if (file == INVALID_HANDLE_VALUE)
            return;

        SYSTEMTIME st = {};
        GetLocalTime(&st);
        char line[1200] = {};
        const int length = sprintf_s(line,
            "%04u-%02u-%02uT%02u:%02u:%02u.%03u [INFO] %s\r\n",
            st.wYear, st.wMonth, st.wDay, st.wHour, st.wMinute, st.wSecond, st.wMilliseconds,
            message ? message : "");
        if (length > 0)
        {
            DWORD written = 0;
            WriteFile(file, line, static_cast<DWORD>(length), &written, nullptr);
        }
        CloseHandle(file);
    }

    bool IsHostElevated()
    {
        HANDLE token = nullptr;
        if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &token))
            return false;
        TOKEN_ELEVATION elevation = {};
        DWORD returned = 0;
        const BOOL ok = GetTokenInformation(token, TokenElevation, &elevation, sizeof(elevation), &returned);
        CloseHandle(token);
        return ok && elevation.TokenIsElevated != 0;
    }

    bool BuildPipeSecurity(SECURITY_ATTRIBUTES& attributes, PSECURITY_DESCRIPTOR& descriptor)
    {
        ZeroMemory(&attributes, sizeof(attributes));
        descriptor = nullptr;

        HANDLE token = nullptr;
        if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &token))
            return false;

        DWORD size = 0;
        GetTokenInformation(token, TokenUser, nullptr, 0, &size);
        if (GetLastError() != ERROR_INSUFFICIENT_BUFFER || size == 0)
        {
            CloseHandle(token);
            return false;
        }

        BYTE* buffer = static_cast<BYTE*>(LocalAlloc(LPTR, size));
        if (!buffer)
        {
            CloseHandle(token);
            return false;
        }

        bool success = false;
        if (GetTokenInformation(token, TokenUser, buffer, size, &size))
        {
            const TOKEN_USER* user = reinterpret_cast<const TOKEN_USER*>(buffer);
            LPSTR sidString = nullptr;
            if (ConvertSidToStringSidA(user->User.Sid, &sidString))
            {
                char sddl[512] = {};
                // Only the owning user plus SYSTEM/Administrators can connect. The protocol itself
                // is also strictly whitelisted; this plugin never exposes arbitrary file/process APIs.
                if (sprintf_s(sddl, "D:P(A;;GA;;;%s)(A;;GA;;;SY)(A;;GA;;;BA)", sidString) > 0 &&
                    ConvertStringSecurityDescriptorToSecurityDescriptorA(
                        sddl, SDDL_REVISION_1, &descriptor, nullptr))
                {
                    attributes.nLength = sizeof(attributes);
                    attributes.lpSecurityDescriptor = descriptor;
                    attributes.bInheritHandle = FALSE;
                    success = true;
                }
                LocalFree(sidString);
            }
        }

        LocalFree(buffer);
        CloseHandle(token);
        return success;
    }

    HANDLE CreateServerPipe()
    {
        SECURITY_ATTRIBUTES attributes = {};
        PSECURITY_DESCRIPTOR descriptor = nullptr;
        SECURITY_ATTRIBUTES* attributesPtr = nullptr;
        if (BuildPipeSecurity(attributes, descriptor))
            attributesPtr = &attributes;

        HANDLE pipe = CreateNamedPipeA(
            kPipePath,
            PIPE_ACCESS_DUPLEX,
            PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT,
            1, 4096, 4096, 0, attributesPtr);

        if (descriptor)
            LocalFree(descriptor);
        return pipe;
    }

    void TrimLine(char* text)
    {
        if (!text)
            return;
        size_t end = strlen(text);
        while (end > 0 && (text[end - 1] == '\r' || text[end - 1] == '\n' || text[end - 1] == ' ' || text[end - 1] == '\t'))
            --end;
        text[end] = '\0';
    }

    void BuildHostInfo(char* output, size_t outputSize)
    {
        char hostPath[MAX_PATH] = {};
        GetModuleFileNameA(nullptr, hostPath, MAX_PATH);
        const char* hostName = strrchr(hostPath, '\\');
        hostName = hostName ? hostName + 1 : hostPath;
        sprintf_s(output, outputSize,
            "PONG|protocol=6|pluginVersion=%s|capabilities=%s|pid=%lu|host=%s|arch=x86|elevated=%s",
            kPluginVersion, kCapabilities, GetCurrentProcessId(), hostName, IsHostElevated() ? "true" : "false");
    }

    bool ResolveApi(RtssApi& api, char* error, size_t errorSize)
    {
        HMODULE hooks = GetModuleHandleA("RTSSHooks.dll");
        if (!hooks)
        {
            sprintf_s(error, errorSize, "RTSSHooks.dll is not loaded in RTSS.exe");
            return false;
        }

        api.loadProfile = reinterpret_cast<LoadProfileProc>(GetProcAddress(hooks, "LoadProfile"));
        api.saveProfile = reinterpret_cast<SaveProfileProc>(GetProcAddress(hooks, "SaveProfile"));
        api.getProperty = reinterpret_cast<GetProfilePropertyProc>(GetProcAddress(hooks, "GetProfileProperty"));
        api.setProperty = reinterpret_cast<SetProfilePropertyProc>(GetProcAddress(hooks, "SetProfileProperty"));
        api.updateProfiles = reinterpret_cast<UpdateProfilesProc>(GetProcAddress(hooks, "UpdateProfiles"));
        api.getFlags = reinterpret_cast<GetFlagsProc>(GetProcAddress(hooks, "GetFlags"));
        api.setFlags = reinterpret_cast<SetFlagsProc>(GetProcAddress(hooks, "SetFlags"));

        if (!api.loadProfile || !api.saveProfile || !api.getProperty || !api.setProperty ||
            !api.updateProfiles || !api.getFlags || !api.setFlags)
        {
            sprintf_s(error, errorSize, "One or more required RTSSHooks exports are unavailable");
            return false;
        }
        return true;
    }

    bool ReadIntProperty(const RtssApi& api, const char* propertyName, int& value)
    {
        value = 0;
        return api.getProperty && api.getProperty(propertyName, reinterpret_cast<LPBYTE>(&value), sizeof(value));
    }

    bool ResolveOsdPresetPosition(int preset, int& x, int& y, char* error, size_t errorSize)
    {
        // RTSS native position selector uses a normalized perimeter grid when
        // CoordinateSpace == 0. RTSS' axes are oriented opposite to screen-space:
        // +X is left, -X is right, +Y is top and -Y is bottom. Keep enum values
        // 0..5 stable and append the two middle-edge positions as 6/7.
        switch (preset)
        {
        case 0: x =  1; y =  1; return true; // Top left
        case 1: x =  0; y =  1; return true; // Top center
        case 2: x = -1; y =  1; return true; // Top right
        case 3: x =  1; y = -1; return true; // Bottom left
        case 4: x =  0; y = -1; return true; // Bottom center
        case 5: x = -1; y = -1; return true; // Bottom right
        case 6: x =  1; y =  0; return true; // Middle left
        case 7: x = -1; y =  0; return true; // Middle right
        default:
            sprintf_s(error, errorSize, "Unsupported OSD position preset %d", preset);
            return false;
        }
    }

    int DetectOsdPositionPreset(int x, int y, int coordinateSpace)
    {
        if (coordinateSpace != 0)
            return -1;
        if (x ==  1 && y ==  1) return 0;
        if (x ==  0 && y ==  1) return 1;
        if (x == -1 && y ==  1) return 2;
        if (x ==  1 && y == -1) return 3;
        if (x ==  0 && y == -1) return 4;
        if (x == -1 && y == -1) return 5;
        if (x ==  1 && y ==  0) return 6;
        if (x == -1 && y ==  0) return 7;
        return -1;
    }

    bool BuildState(const RtssApi& api, char* output, size_t outputSize, char* error, size_t errorSize)
    {
        api.loadProfile("");

        int frameLimit = 0;
        int syncLimiter = 0;
        int zoomRatio = 0;
        int positionX = 0;
        int positionY = 0;
        int coordinateSpace = 0;
        if (!ReadIntProperty(api, "FramerateLimit", frameLimit))
        {
            sprintf_s(error, errorSize, "GetProfileProperty(FramerateLimit) failed");
            return false;
        }
        if (!ReadIntProperty(api, "SyncLimiter", syncLimiter))
        {
            sprintf_s(error, errorSize, "GetProfileProperty(SyncLimiter) failed");
            return false;
        }
        if (!ReadIntProperty(api, "ZoomRatio", zoomRatio))
        {
            sprintf_s(error, errorSize, "GetProfileProperty(ZoomRatio) failed");
            return false;
        }
        if (!ReadIntProperty(api, "PositionX", positionX) || !ReadIntProperty(api, "PositionY", positionY))
        {
            sprintf_s(error, errorSize, "GetProfileProperty(PositionX/PositionY) failed");
            return false;
        }
        if (!ReadIntProperty(api, "CoordinateSpace", coordinateSpace))
        {
            sprintf_s(error, errorSize, "GetProfileProperty(CoordinateSpace) failed");
            return false;
        }

        const int positionPreset = DetectOsdPositionPreset(positionX, positionY, coordinateSpace);
        const DWORD flags = api.getFlags();
        sprintf_s(output, outputSize,
            "STATE|protocol=6|pluginVersion=%s|frameLimit=%d|syncLimiter=%d|zoomRatio=%d|positionPreset=%d|flags=%lu",
            kPluginVersion, frameLimit, syncLimiter, zoomRatio, positionPreset, flags);
        return true;
    }

    bool SetIntPropertyVerified(const RtssApi& api, const char* propertyName, int value, char* error, size_t errorSize)
    {
        api.loadProfile("");
        int mutableValue = value;
        if (!api.setProperty(propertyName, reinterpret_cast<LPBYTE>(&mutableValue), sizeof(mutableValue)))
        {
            sprintf_s(error, errorSize, "SetProfileProperty(%s) rejected value %d", propertyName, value);
            return false;
        }

        api.saveProfile("");
        api.updateProfiles();
        api.loadProfile("");

        int readBack = 0;
        if (!ReadIntProperty(api, propertyName, readBack))
        {
            sprintf_s(error, errorSize, "Could not read back %s", propertyName);
            return false;
        }
        if (readBack != value)
        {
            sprintf_s(error, errorSize, "%s persistence verification failed: requested=%d readBack=%d", propertyName, value, readBack);
            return false;
        }
        return true;
    }

    bool SetOsdPositionVerified(const RtssApi& api, int preset, char* error, size_t errorSize)
    {
        int positionX = 0;
        int positionY = 0;
        if (!ResolveOsdPresetPosition(preset, positionX, positionY, error, errorSize))
            return false;

        api.loadProfile("");
        int mutableX = positionX;
        int mutableY = positionY;
        int mutableCoordinateSpace = 0;
        if (!api.setProperty("PositionX", reinterpret_cast<LPBYTE>(&mutableX), sizeof(mutableX)))
        {
            sprintf_s(error, errorSize, "SetProfileProperty(PositionX) rejected value %d", positionX);
            return false;
        }
        if (!api.setProperty("PositionY", reinterpret_cast<LPBYTE>(&mutableY), sizeof(mutableY)))
        {
            api.loadProfile("");
            sprintf_s(error, errorSize, "SetProfileProperty(PositionY) rejected value %d", positionY);
            return false;
        }
        if (!api.setProperty("CoordinateSpace", reinterpret_cast<LPBYTE>(&mutableCoordinateSpace), sizeof(mutableCoordinateSpace)))
        {
            api.loadProfile("");
            sprintf_s(error, errorSize, "SetProfileProperty(CoordinateSpace) rejected native preset space 0");
            return false;
        }

        api.saveProfile("");
        api.updateProfiles();
        api.loadProfile("");

        int readBackX = 0;
        int readBackY = 0;
        int readBackCoordinateSpace = -1;
        if (!ReadIntProperty(api, "PositionX", readBackX) ||
            !ReadIntProperty(api, "PositionY", readBackY) ||
            !ReadIntProperty(api, "CoordinateSpace", readBackCoordinateSpace))
        {
            sprintf_s(error, errorSize, "Could not read back OSD PositionX/PositionY/CoordinateSpace");
            return false;
        }
        if (readBackX != positionX || readBackY != positionY || readBackCoordinateSpace != 0)
        {
            sprintf_s(error, errorSize,
                "OSD position persistence verification failed: requested=%d,%d,space0 readBack=%d,%d,space%d",
                positionX, positionY, readBackX, readBackY, readBackCoordinateSpace);
            return false;
        }
        return true;
    }

    bool SetFlagVerified(const RtssApi& api, DWORD bit, bool enabled, char* error, size_t errorSize)
    {
        const DWORD before = api.getFlags();
        api.setFlags(~bit, enabled ? bit : 0u);
        const DWORD after = api.getFlags();
        const bool actual = (after & bit) != 0;
        if (actual != enabled)
        {
            sprintf_s(error, errorSize,
                "RTSS flag verification failed bit=0x%08lX requested=%s before=0x%08lX after=0x%08lX",
                bit, enabled ? "true" : "false", before, after);
            return false;
        }
        return true;
    }

    bool ParseIntValue(const char* request, const char* prefix, int minValue, int maxValue, int& value)
    {
        const size_t prefixLength = strlen(prefix);
        if (_strnicmp(request, prefix, prefixLength) != 0)
            return false;
        const char* text = request + prefixLength;
        char* end = nullptr;
        const long parsed = strtol(text, &end, 10);
        if (end == text || *end != '\0' || parsed < minValue || parsed > maxValue)
            return false;
        value = static_cast<int>(parsed);
        return true;
    }

    BOOL CALLBACK CloseRtssWindowProc(HWND hWnd, LPARAM lParam)
    {
        DWORD owner = 0;
        GetWindowThreadProcessId(hWnd, &owner);
        if (owner == static_cast<DWORD>(lParam))
            PostMessageA(hWnd, WM_CLOSE, 0, 0);
        return TRUE;
    }

    DWORD WINAPI DelayedCloseProc(LPVOID)
    {
        Sleep(120);
        EnumWindows(CloseRtssWindowProc, static_cast<LPARAM>(GetCurrentProcessId()));
        return 0;
    }

    void ScheduleHostClose()
    {
        HANDLE thread = CreateThread(nullptr, 0, DelayedCloseProc, nullptr, 0, nullptr);
        if (thread)
            CloseHandle(thread);
    }

    void ProcessRequest(const char* request, char* response, size_t responseSize, bool& closeRequested)
    {
        closeRequested = false;
        if (_stricmp(request, "PING") == 0 || _stricmp(request, "INFO") == 0)
        {
            BuildHostInfo(response, responseSize);
            return;
        }

        if (_stricmp(request, "CLOSE_RTSS") == 0)
        {
            sprintf_s(response, responseSize, "OK|code=closing|pluginVersion=%s", kPluginVersion);
            closeRequested = true;
            return;
        }

        ApiLock apiLock;
        RtssApi api;
        char error[320] = {};
        if (!ResolveApi(api, error, sizeof(error)))
        {
            sprintf_s(response, responseSize, "ERROR|code=api_unavailable|message=%s", error);
            LogLine(error);
            return;
        }

        if (_stricmp(request, "GET_STATE") == 0)
        {
            if (!BuildState(api, response, responseSize, error, sizeof(error)))
                sprintf_s(response, responseSize, "ERROR|code=read_state_failed|message=%s", error);
            return;
        }

        int value = 0;
        bool changed = false;
        if (ParseIntValue(request, "SET_FRAME_LIMIT|value=", 0, 1000, value))
        {
            changed = SetIntPropertyVerified(api, "FramerateLimit", value, error, sizeof(error));
        }
        else if (ParseIntValue(request, "SET_SYNC_LIMITER|value=", 0, 3, value))
        {
            changed = SetIntPropertyVerified(api, "SyncLimiter", value, error, sizeof(error));
        }
        else if (ParseIntValue(request, "SET_OSD_ZOOM|value=", 1, 8, value))
        {
            changed = SetIntPropertyVerified(api, "ZoomRatio", value, error, sizeof(error));
        }
        else if (ParseIntValue(request, "SET_OSD_POSITION|value=", 0, 7, value))
        {
            changed = SetOsdPositionVerified(api, value, error, sizeof(error));
        }
        else if (ParseIntValue(request, "SET_LIMITER_ENABLED|value=", 0, 1, value))
        {
            // Limiter enabled is the inverse of RTSS' disabled bit.
            changed = SetFlagVerified(api, kFlagLimiterDisabled, value == 0, error, sizeof(error));
        }
        else if (ParseIntValue(request, "SET_OSD_VISIBLE|value=", 0, 1, value))
        {
            changed = SetFlagVerified(api, kFlagOsdVisible, value != 0, error, sizeof(error));
        }
        else
        {
            sprintf_s(response, responseSize, "ERROR|code=unsupported_command|message=%s", request);
            return;
        }

        if (!changed)
        {
            sprintf_s(response, responseSize, "ERROR|code=write_failed|message=%s", error[0] ? error : "RTSS mutation failed");
            LogLine(error[0] ? error : "RTSS mutation failed");
            return;
        }

        if (!BuildState(api, response, responseSize, error, sizeof(error)))
            sprintf_s(response, responseSize, "ERROR|code=post_write_state_failed|message=%s", error);
    }

    DWORD WINAPI ServerThreadProc(LPVOID)
    {
        char startInfo[768] = {};
        BuildHostInfo(startInfo, sizeof(startInfo));
        LogLine(startInfo);
        LogLine("Named pipe server started on RTSSGameBar.RTSSPlugin.v6. Global-profile control backend v1.0.0.");

        while (WaitForSingleObject(g_stopEvent, 0) == WAIT_TIMEOUT)
        {
            HANDLE pipe = CreateServerPipe();
            if (pipe == INVALID_HANDLE_VALUE)
            {
                char error[256] = {};
                sprintf_s(error, "CreateNamedPipe failed. Win32Error=%lu", GetLastError());
                LogLine(error);
                Sleep(500);
                continue;
            }

            BOOL connected = ConnectNamedPipe(pipe, nullptr);
            if (!connected && GetLastError() == ERROR_PIPE_CONNECTED)
                connected = TRUE;

            if (!connected)
            {
                CloseHandle(pipe);
                if (WaitForSingleObject(g_stopEvent, 0) == WAIT_OBJECT_0)
                    break;
                continue;
            }

            char request[512] = {};
            DWORD bytesRead = 0;
            const BOOL readOk = ReadFile(pipe, request, sizeof(request) - 1, &bytesRead, nullptr);
            bool closeRequested = false;
            if (readOk && bytesRead > 0)
            {
                request[bytesRead] = '\0';
                TrimLine(request);
                char response[1280] = {};
                ProcessRequest(request, response, sizeof(response), closeRequested);
                strcat_s(response, "\n");
                DWORD bytesWritten = 0;
                WriteFile(pipe, response, static_cast<DWORD>(strlen(response)), &bytesWritten, nullptr);
                FlushFileBuffers(pipe);
            }

            DisconnectNamedPipe(pipe);
            CloseHandle(pipe);

            if (closeRequested)
            {
                LogLine("Graceful RTSS close requested by RTSS Game Bar.");
                ScheduleHostClose();
            }
        }

        LogLine("Named pipe server stopped.");
        return 0;
    }

    void WakeServerThread()
    {
        HANDLE pipe = CreateFileA(kPipePath, GENERIC_READ | GENERIC_WRITE, 0, nullptr, OPEN_EXISTING, 0, nullptr);
        if (pipe != INVALID_HANDLE_VALUE)
        {
            const char* wake = "STOP\n";
            DWORD written = 0;
            WriteFile(pipe, wake, static_cast<DWORD>(strlen(wake)), &written, nullptr);
            CloseHandle(pipe);
        }
    }
}

extern "C" BOOL APIENTRY DllMain(HMODULE, DWORD, LPVOID)
{
    return TRUE;
}

RTSSGAMEBAR_PLUGIN_API BOOL Start()
{
    if (g_serverThread)
        return TRUE;

    g_stopEvent = CreateEventA(nullptr, TRUE, FALSE, nullptr);
    if (!g_stopEvent)
        return FALSE;

    g_serverThread = CreateThread(nullptr, 0, ServerThreadProc, nullptr, 0, nullptr);
    if (!g_serverThread)
    {
        CloseHandle(g_stopEvent);
        g_stopEvent = nullptr;
        return FALSE;
    }

    return TRUE;
}

RTSSGAMEBAR_PLUGIN_API void Stop()
{
    if (!g_serverThread)
        return;

    SetEvent(g_stopEvent);
    WakeServerThread();
    WaitForSingleObject(g_serverThread, 3000);
    CloseHandle(g_serverThread);
    g_serverThread = nullptr;
    CloseHandle(g_stopEvent);
    g_stopEvent = nullptr;
}

RTSSGAMEBAR_PLUGIN_API BOOL Setup(HWND hWnd)
{
    if (!hWnd)
        return TRUE;

    MessageBoxA(
        hWnd,
        "RTSS Game Bar integration plugin v1.0.0\n\n"
        "This minimal client plugin exposes a user-scoped, command-whitelisted local bridge for Xbox Game Bar. "
        "RTSS profile mutations are executed inside RTSS and verified with read-back.",
        "RTSS Game Bar",
        MB_OK | MB_ICONINFORMATION);
    return TRUE;
}
