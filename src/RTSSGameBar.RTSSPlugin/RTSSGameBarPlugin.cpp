#include "RTSSGameBarPlugin.h"

#include <Aclapi.h>
#include <Sddl.h>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <string>
#include <vector>

namespace
{
    const char* kPipePath = "\\\\.\\pipe\\RTSSGameBar.RTSSPlugin.v7";
    const char* kLogName = "RTSSGameBar-RTSSPlugin.log";
    const char* kPluginVersion = "1.1.0";
    const char* kCapabilities = "state,profiles,frameLimit,limiterType,limiterEnabled,osdVisible,osdZoom,osdPosition,closeRtss";
    const DWORD kFlagOsdVisible = 1u;
    const DWORD kFlagLimiterDisabled = 4u;
    const DWORD kMaxProfileListBytes = 12288u;

    HANDLE g_stopEvent = nullptr;
    HANDLE g_serverThread = nullptr;
    SRWLOCK g_rtssApiLock = SRWLOCK_INIT;

    typedef DWORD (*EnumProfilesProc)(LPSTR, DWORD);
    typedef void (*LoadProfileProc)(LPCSTR);
    typedef void (*SaveProfileProc)(LPCSTR);
    typedef BOOL (*GetProfilePropertyProc)(LPCSTR, LPBYTE, DWORD);
    typedef BOOL (*SetProfilePropertyProc)(LPCSTR, LPBYTE, DWORD);
    typedef void (*UpdateProfilesProc)();
    typedef DWORD (*GetFlagsProc)();
    typedef DWORD (*SetFlagsProc)(DWORD, DWORD);

    struct RtssApi
    {
        EnumProfilesProc enumProfiles = nullptr;
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
            "PONG|protocol=7|pluginVersion=%s|capabilities=%s|pid=%lu|host=%s|arch=x86|elevated=%s",
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
        // EnumProfiles is optional so an older RTSSHooks build still exposes Global control,
        // just without per-profile enumeration.
        api.enumProfiles = reinterpret_cast<EnumProfilesProc>(GetProcAddress(hooks, "EnumProfiles"));

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

    // Profile names are arbitrary RTSS strings, so every value carried over the key=value pipe
    // protocol is percent-encoded. This keeps '|' and '=' unambiguous for the helper parser.
    std::string UrlEncode(const std::string& value)
    {
        static const char* hex = "0123456789ABCDEF";
        std::string result;
        result.reserve(value.size());
        for (size_t i = 0; i < value.size(); ++i)
        {
            const unsigned char c = static_cast<unsigned char>(value[i]);
            const bool unreserved = (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') ||
                c == '-' || c == '_' || c == '.' || c == '~';
            if (unreserved)
            {
                result.push_back(static_cast<char>(c));
            }
            else
            {
                result.push_back('%');
                result.push_back(hex[(c >> 4) & 0x0F]);
                result.push_back(hex[c & 0x0F]);
            }
        }
        return result;
    }

    std::string UrlDecode(const std::string& value)
    {
        std::string result;
        result.reserve(value.size());
        for (size_t i = 0; i < value.size(); ++i)
        {
            const char c = value[i];
            if (c == '%' && i + 2 < value.size())
            {
                char hex[3] = { value[i + 1], value[i + 2], '\0' };
                char* end = nullptr;
                const long parsed = strtol(hex, &end, 16);
                if (end == hex + 2)
                {
                    result.push_back(static_cast<char>(parsed));
                    i += 2;
                    continue;
                }
            }
            result.push_back(c);
        }
        return result;
    }

    std::string FirstToken(const char* request)
    {
        if (!request)
            return std::string();
        const char* separator = strchr(request, '|');
        return separator ? std::string(request, separator - request) : std::string(request);
    }

    typedef std::vector<std::pair<std::string, std::string> > RequestParams;

    RequestParams ParseParams(const char* request)
    {
        RequestParams params;
        if (!request)
            return params;

        const std::string text(request);
        size_t position = text.find('|');
        while (position != std::string::npos)
        {
            const size_t next = text.find('|', position + 1);
            const std::string part = text.substr(position + 1,
                next == std::string::npos ? std::string::npos : next - position - 1);
            const size_t separator = part.find('=');
            if (separator != std::string::npos && separator > 0)
                params.push_back(std::make_pair(part.substr(0, separator), part.substr(separator + 1)));
            position = next;
        }
        return params;
    }

    bool TryGetParam(const RequestParams& params, const char* name, std::string& value)
    {
        for (size_t i = 0; i < params.size(); ++i)
        {
            if (_stricmp(params[i].first.c_str(), name) == 0)
            {
                value = UrlDecode(params[i].second);
                return true;
            }
        }
        return false;
    }

    bool TryGetIntParam(const RequestParams& params, const char* name, int minValue, int maxValue, int& value)
    {
        std::string text;
        if (!TryGetParam(params, name, text) || text.empty())
            return false;

        char* end = nullptr;
        const long parsed = strtol(text.c_str(), &end, 10);
        if (end == text.c_str() || *end != '\0' || parsed < minValue || parsed > maxValue)
            return false;

        value = static_cast<int>(parsed);
        return true;
    }

    bool EnumerateProfiles(const RtssApi& api, std::vector<std::string>& profiles, char* error, size_t errorSize)
    {
        profiles.clear();
        if (!api.enumProfiles)
            return true;

        DWORD size = 512;
        for (int attempt = 0; attempt < 4; ++attempt)
        {
            std::vector<char> buffer(size + 2, 0);
            const DWORD required = api.enumProfiles(buffer.data(), static_cast<DWORD>(buffer.size()));
            if (required == 0)
                return true;
            if (required > buffer.size())
            {
                if (required > 65536)
                {
                    sprintf_s(error, errorSize, "EnumProfiles required an unexpected %lu bytes", required);
                    return false;
                }
                size = required + 2;
                continue;
            }

            char* context = nullptr;
            for (char* token = strtok_s(buffer.data(), ",", &context); token;
                 token = strtok_s(nullptr, ",", &context))
            {
                std::string name(token);
                const size_t start = name.find_first_not_of(" \t");
                const size_t end = name.find_last_not_of(" \t");
                if (start == std::string::npos)
                    continue;
                name = name.substr(start, end - start + 1);
                if (name.empty() || _stricmp(name.c_str(), "Global") == 0)
                    continue;

                bool duplicate = false;
                for (size_t i = 0; i < profiles.size(); ++i)
                {
                    if (_stricmp(profiles[i].c_str(), name.c_str()) == 0)
                    {
                        duplicate = true;
                        break;
                    }
                }
                if (!duplicate)
                    profiles.push_back(name);
            }
            return true;
        }

        sprintf_s(error, errorSize, "EnumProfiles buffer could not be sized");
        return false;
    }

    bool ContainsProfile(const std::vector<std::string>& profiles, const std::string& profile)
    {
        for (size_t i = 0; i < profiles.size(); ++i)
        {
            if (_stricmp(profiles[i].c_str(), profile.c_str()) == 0)
                return true;
        }
        return false;
    }

    std::string StripExeSuffix(std::string name)
    {
        if (name.size() > 4 && _stricmp(name.c_str() + name.size() - 4, ".exe") == 0)
            name.resize(name.size() - 4);
        return name;
    }

    // RTSS creates per-application profiles named after the executable (for example
    // "Cyberpunk2077.exe"). This only produces a UI hint; it never decides which profile is
    // written to.
    std::string FindProfileForHint(const std::vector<std::string>& profiles, const std::string& executable)
    {
        if (executable.empty())
            return std::string();

        const std::string executableBase = StripExeSuffix(executable);
        for (size_t i = 0; i < profiles.size(); ++i)
        {
            if (_stricmp(profiles[i].c_str(), executable.c_str()) == 0)
                return profiles[i];
        }
        for (size_t i = 0; i < profiles.size(); ++i)
        {
            if (_stricmp(StripExeSuffix(profiles[i]).c_str(), executableBase.c_str()) == 0)
                return profiles[i];
        }
        return std::string();
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

    bool BuildState(const RtssApi& api, const std::string& requestedProfile, const std::string& hintExecutable,
        char* output, size_t outputSize, char* error, size_t errorSize)
    {
        std::vector<std::string> profiles;
        if (!EnumerateProfiles(api, profiles, error, errorSize))
        {
            // Keep the read-only state usable: the widget then reports the requested profile as
            // missing instead of failing the whole status request. Writes still refuse to run.
            LogLine(error);
            error[0] = '\0';
        }

        std::string effectiveProfile = requestedProfile;
        std::string missingProfile;
        if (!effectiveProfile.empty() && !ContainsProfile(profiles, effectiveProfile))
        {
            // Never label Global values with a profile name that does not exist.
            missingProfile = effectiveProfile;
            effectiveProfile.clear();
        }

        api.loadProfile(effectiveProfile.c_str());

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
        const std::string activeHint = FindProfileForHint(profiles, hintExecutable);

        std::string list;
        int appended = 0;
        bool truncated = false;
        for (size_t i = 0; i < profiles.size(); ++i)
        {
            char index[32] = {};
            sprintf_s(index, "|p%d=", appended);
            const std::string entry = std::string(index) + UrlEncode(profiles[i]);
            if (list.size() + entry.size() > kMaxProfileListBytes)
            {
                truncated = true;
                break;
            }
            list += entry;
            ++appended;
        }

        char head[4096] = {};
        sprintf_s(head,
            "STATE|protocol=7|pluginVersion=%s|frameLimit=%d|syncLimiter=%d|zoomRatio=%d|positionPreset=%d|flags=%lu|profileCount=%d|truncated=%d|profile=%s|profileMissing=%s|activeHint=%s",
            kPluginVersion, frameLimit, syncLimiter, zoomRatio, positionPreset, flags, appended,
            truncated ? 1 : 0, UrlEncode(effectiveProfile).c_str(), UrlEncode(missingProfile).c_str(),
            UrlEncode(activeHint).c_str());

        const std::string response = std::string(head) + list;
        if (response.size() + 2 > outputSize)
        {
            sprintf_s(error, errorSize, "State response exceeded %lu bytes", static_cast<unsigned long>(outputSize));
            return false;
        }

        memcpy(output, response.c_str(), response.size() + 1);
        return true;
    }

    bool SetIntPropertyVerified(const RtssApi& api, const std::string& profile, const char* propertyName, int value, char* error, size_t errorSize)
    {
        api.loadProfile(profile.c_str());
        int mutableValue = value;
        if (!api.setProperty(propertyName, reinterpret_cast<LPBYTE>(&mutableValue), sizeof(mutableValue)))
        {
            sprintf_s(error, errorSize, "SetProfileProperty(%s) rejected value %d", propertyName, value);
            return false;
        }

        api.saveProfile(profile.c_str());
        api.updateProfiles();
        api.loadProfile(profile.c_str());

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

    bool SetOsdPositionVerified(const RtssApi& api, const std::string& profile, int preset, char* error, size_t errorSize)
    {
        int positionX = 0;
        int positionY = 0;
        if (!ResolveOsdPresetPosition(preset, positionX, positionY, error, errorSize))
            return false;

        api.loadProfile(profile.c_str());
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
            api.loadProfile(profile.c_str());
            sprintf_s(error, errorSize, "SetProfileProperty(PositionY) rejected value %d", positionY);
            return false;
        }
        if (!api.setProperty("CoordinateSpace", reinterpret_cast<LPBYTE>(&mutableCoordinateSpace), sizeof(mutableCoordinateSpace)))
        {
            api.loadProfile(profile.c_str());
            sprintf_s(error, errorSize, "SetProfileProperty(CoordinateSpace) rejected native preset space 0");
            return false;
        }

        api.saveProfile(profile.c_str());
        api.updateProfiles();
        api.loadProfile(profile.c_str());

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

    bool ReadRequestedValue(const char* request, const RequestParams& params, const char* legacyPrefix,
        int minValue, int maxValue, int& value)
    {
        if (TryGetIntParam(params, "value", minValue, maxValue, value))
            return true;
        return ParseIntValue(request, legacyPrefix, minValue, maxValue, value);
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
        const std::string command = FirstToken(request);
        if (_stricmp(command.c_str(), "PING") == 0 || _stricmp(command.c_str(), "INFO") == 0)
        {
            BuildHostInfo(response, responseSize);
            return;
        }

        if (_stricmp(command.c_str(), "CLOSE_RTSS") == 0)
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

        const RequestParams params = ParseParams(request);

        if (_stricmp(command.c_str(), "GET_STATE") == 0)
        {
            std::string profile;
            std::string hint;
            TryGetParam(params, "profile", profile);
            TryGetParam(params, "hint", hint);
            if (!BuildState(api, profile, hint, response, responseSize, error, sizeof(error)))
                sprintf_s(response, responseSize, "ERROR|code=read_state_failed|message=%s", error);
            return;
        }

        std::string profile;
        TryGetParam(params, "profile", profile);

        if (!profile.empty())
        {
            // LoadProfile silently falls back to Global for unknown names, so validate first and
            // refuse the write instead of touching Global on a typo.
            std::vector<std::string> profiles;
            if (!EnumerateProfiles(api, profiles, error, sizeof(error)))
            {
                sprintf_s(response, responseSize, "ERROR|code=profile_enum_failed|message=%s", error);
                return;
            }
            if (!ContainsProfile(profiles, profile))
            {
                sprintf_s(response, responseSize, "ERROR|code=profile_not_found|message=%s",
                    profile.c_str());
                return;
            }
        }

        int value = 0;
        bool changed = false;
        if (_stricmp(command.c_str(), "SET_FRAME_LIMIT") == 0 &&
            ReadRequestedValue(request, params, "SET_FRAME_LIMIT|value=", 0, 1000, value))
        {
            changed = SetIntPropertyVerified(api, profile, "FramerateLimit", value, error, sizeof(error));
        }
        else if (_stricmp(command.c_str(), "SET_SYNC_LIMITER") == 0 &&
            ReadRequestedValue(request, params, "SET_SYNC_LIMITER|value=", 0, 3, value))
        {
            changed = SetIntPropertyVerified(api, profile, "SyncLimiter", value, error, sizeof(error));
        }
        else if (_stricmp(command.c_str(), "SET_OSD_ZOOM") == 0 &&
            ReadRequestedValue(request, params, "SET_OSD_ZOOM|value=", 1, 8, value))
        {
            changed = SetIntPropertyVerified(api, profile, "ZoomRatio", value, error, sizeof(error));
        }
        else if (_stricmp(command.c_str(), "SET_OSD_POSITION") == 0 &&
            ReadRequestedValue(request, params, "SET_OSD_POSITION|value=", 0, 7, value))
        {
            changed = SetOsdPositionVerified(api, profile, value, error, sizeof(error));
        }
        else if (_stricmp(command.c_str(), "SET_LIMITER_ENABLED") == 0 &&
            ReadRequestedValue(request, params, "SET_LIMITER_ENABLED|value=", 0, 1, value))
        {
            // Limiter enabled is the inverse of RTSS' disabled bit. This flag is global on
            // purpose: RTSS exposes it as a runtime flag, not as a profile property.
            changed = SetFlagVerified(api, kFlagLimiterDisabled, value == 0, error, sizeof(error));
        }
        else if (_stricmp(command.c_str(), "SET_OSD_VISIBLE") == 0 &&
            ReadRequestedValue(request, params, "SET_OSD_VISIBLE|value=", 0, 1, value))
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

        if (!BuildState(api, profile, std::string(), response, responseSize, error, sizeof(error)))
            sprintf_s(response, responseSize, "ERROR|code=post_write_state_failed|message=%s", error);
    }

    DWORD WINAPI ServerThreadProc(LPVOID)
    {
        char startInfo[768] = {};
        BuildHostInfo(startInfo, sizeof(startInfo));
        LogLine(startInfo);
        LogLine("Named pipe server started on RTSSGameBar.RTSSPlugin.v7. Profile-aware control backend v1.1.0.");

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

            char request[2048] = {};
            DWORD bytesRead = 0;
            const BOOL readOk = ReadFile(pipe, request, sizeof(request) - 1, &bytesRead, nullptr);
            bool closeRequested = false;
            if (readOk && bytesRead > 0)
            {
                request[bytesRead] = '\0';
                TrimLine(request);
                char response[16384] = {};
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
        "RTSS Game Bar integration plugin v1.1.0\n\n"
        "This minimal client plugin exposes a user-scoped, command-whitelisted local bridge for Xbox Game Bar. "
        "RTSS profile mutations are executed inside RTSS and verified with read-back. Global and per-application "
        "profiles are supported; profile names are enumerated from RTSS.",
        "RTSS Game Bar",
        MB_OK | MB_ICONINFORMATION);
    return TRUE;
}
