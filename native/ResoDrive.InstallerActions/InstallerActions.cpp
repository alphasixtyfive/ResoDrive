#include <windows.h>
#include <msi.h>
#include <msiquery.h>
#include <sddl.h>
#include <objbase.h>
#include <userenv.h>
#include <string>
#include <vector>
#include <stdexcept>
#include <system_error>
#include <cstring>
#include <algorithm>
#include <utility>

namespace
{
    HINSTANCE module;
    constexpr WORD ApplicationResource = 101;
    constexpr WORD PolicyResource = 102;
#ifdef RESODRIVE_INSTALLER_ACTIONS_TESTING
    void (*AfterPayloadWrite)(const std::wstring&) = nullptr;
#endif

    class Handle
    {
        HANDLE value = INVALID_HANDLE_VALUE;
    public:
        explicit Handle(HANDLE handle = INVALID_HANDLE_VALUE) : value(handle) {}
        ~Handle() { reset(); }
        Handle(const Handle&) = delete;
        Handle& operator=(const Handle&) = delete;
        Handle(Handle&& other) noexcept : value(other.value) { other.value = INVALID_HANDLE_VALUE; }
        HANDLE get() const { return value; }
        bool valid() const { return value != nullptr && value != INVALID_HANDLE_VALUE; }
        void reset(HANDLE handle = INVALID_HANDLE_VALUE)
        {
            if (valid()) CloseHandle(value);
            value = handle;
        }
    };

    [[noreturn]] void Fail(const char* operation, DWORD error = GetLastError())
    {
        throw std::system_error(static_cast<int>(error), std::system_category(), operation);
    }

    void Log(MSIHANDLE installation, const std::wstring& message)
    {
        const MSIHANDLE record = MsiCreateRecord(1);
        if (record)
        {
            MsiRecordSetStringW(record, 0, L"[1]");
            MsiRecordSetStringW(record, 1, message.c_str());
            MsiProcessMessage(installation, INSTALLMESSAGE_INFO, record);
            MsiCloseHandle(record);
        }
    }

    std::wstring Property(MSIHANDLE installation, const wchar_t* name)
    {
        DWORD length = 0;
        wchar_t empty = L'\0';
        const UINT result = MsiGetPropertyW(installation, name, &empty, &length);
        if (result != ERROR_SUCCESS && result != ERROR_MORE_DATA)
            Fail("MsiGetProperty", result);
        std::vector<wchar_t> buffer(static_cast<size_t>(length) + 1);
        DWORD capacity = static_cast<DWORD>(buffer.size());
        const UINT read = MsiGetPropertyW(installation, name, buffer.data(), &capacity);
        if (read != ERROR_SUCCESS) Fail("MsiGetProperty", read);
        return std::wstring(buffer.data(), capacity);
    }

    bool OptOut(const std::wstring& value)
    {
        if (value == L"0") return false;
        if (value == L"1") return true;
        throw std::runtime_error("RDRIVE_DISABLE_CET must be 0 or 1");
    }

    std::wstring AbsolutePath(const std::wstring& value)
    {
        // Control characters also make newline-delimited CustomActionData invalid.
        if (value.empty() || value.find_first_of(L"\r\n\t") != std::wstring::npos ||
            value.find(L'\0') != std::wstring::npos)
            throw std::runtime_error("The installer path is invalid");
        for (const wchar_t ch : value)
            if (ch < 32 || ch == L'"') throw std::runtime_error("The installer path is invalid");
        const bool drive = value.size() >= 3 && value[1] == L':' &&
            (value[2] == L'\\' || value[2] == L'/');
        const bool network = value.size() > 2 && value[0] == L'\\' && value[1] == L'\\';
        if (!drive && !network) throw std::runtime_error("The installer path must be absolute");
        const DWORD length = GetFullPathNameW(value.c_str(), 0, nullptr, nullptr);
        if (!length) Fail("GetFullPathName");
        std::vector<wchar_t> buffer(length);
        const DWORD read = GetFullPathNameW(value.c_str(), length, buffer.data(), nullptr);
        if (!read || read >= length) Fail("GetFullPathName");
        return std::wstring(buffer.data(), read);
    }

    std::wstring Quote(const std::wstring& argument)
    {
        std::wstring result = L"\"";
        size_t slashes = 0;
        for (const wchar_t ch : argument)
        {
            if (ch == L'\\') { ++slashes; continue; }
            result.append(ch == L'"' ? slashes * 2 + 1 : slashes, L'\\');
            slashes = 0;
            result += ch;
        }
        result.append(slashes * 2, L'\\');
        return result + L'"';
    }

    bool SupportsCet()
    {
        using GetVersion = LONG(WINAPI*)(OSVERSIONINFOW*);
        const HMODULE ntdll = GetModuleHandleW(L"ntdll.dll");
        if (!ntdll) Fail("GetModuleHandle(ntdll)");
#pragma warning(suppress: 4191) // The documented RtlGetVersion ABI.
        const auto getVersion = reinterpret_cast<GetVersion>(GetProcAddress(ntdll, "RtlGetVersion"));
        if (!getVersion) Fail("GetProcAddress(RtlGetVersion)");
        OSVERSIONINFOW version{};
        version.dwOSVersionInfoSize = sizeof(version);
        if (getVersion(&version) < 0) throw std::runtime_error("Could not determine Windows CET support");
        return version.dwMajorVersion > 10 ||
            (version.dwMajorVersion == 10 && version.dwBuildNumber >= 19041);
    }

    DWORD Run(const std::wstring& executable, const std::vector<std::wstring>& arguments, bool optOut,
        bool interactive = false)
    {
        std::wstring command = Quote(executable);
        for (const auto& argument : arguments) command += L" " + Quote(argument);
        STARTUPINFOEXW start{};
        start.StartupInfo.cb = sizeof(STARTUPINFOW);
        wchar_t interactiveDesktop[] = L"winsta0\\default";
        if (interactive) start.StartupInfo.lpDesktop = interactiveDesktop;
        std::vector<BYTE> attributeStorage;
        DWORD flags = CREATE_NO_WINDOW;
        // Windows creates the shadow stack before CoreCLR loads. Managed Main
        // and SetProcessMitigationPolicy are too late to disable this feature.
        DWORD64 policy[2] = { 0, PROCESS_CREATION_MITIGATION_POLICY2_CET_USER_SHADOW_STACKS_ALWAYS_OFF };
        if (optOut && SupportsCet())
        {
            SIZE_T size = 0;
            InitializeProcThreadAttributeList(nullptr, 1, 0, &size);
            if (!size) Fail("InitializeProcThreadAttributeList(size)");
            attributeStorage.resize(size);
            start.lpAttributeList = reinterpret_cast<LPPROC_THREAD_ATTRIBUTE_LIST>(attributeStorage.data());
            if (!InitializeProcThreadAttributeList(start.lpAttributeList, 1, 0, &size))
                Fail("InitializeProcThreadAttributeList");
            if (!UpdateProcThreadAttribute(start.lpAttributeList, 0, PROC_THREAD_ATTRIBUTE_MITIGATION_POLICY,
                policy, sizeof(policy), nullptr, nullptr))
            {
                const DWORD error = GetLastError();
                DeleteProcThreadAttributeList(start.lpAttributeList);
                Fail("UpdateProcThreadAttribute(CET off)", error);
            }
            start.StartupInfo.cb = sizeof(start);
            flags |= EXTENDED_STARTUPINFO_PRESENT;
        }
        PROCESS_INFORMATION child{};
        HANDLE rawImpersonation = nullptr;
        Handle impersonation;
        Handle primary;
        void* environment = nullptr;
        BOOL created = FALSE;
        if (OpenThreadToken(GetCurrentThread(), TOKEN_QUERY | TOKEN_DUPLICATE, TRUE, &rawImpersonation))
        {
            // CreateProcess alone ignores thread impersonation. MSI's immediate
            // actions must retain their invoking account, rather than start the
            // helper with an installer-service SYSTEM token.
            impersonation.reset(rawImpersonation);
            HANDLE rawPrimary = nullptr;
            if (!DuplicateTokenEx(impersonation.get(), TOKEN_QUERY | TOKEN_DUPLICATE | TOKEN_ASSIGN_PRIMARY,
                nullptr, SecurityImpersonation, TokenPrimary, &rawPrimary))
            {
                const DWORD tokenError = GetLastError();
                if (start.lpAttributeList) DeleteProcThreadAttributeList(start.lpAttributeList);
                Fail("DuplicateTokenEx(installer account)", tokenError);
            }
            primary.reset(rawPrimary);
            if (!CreateEnvironmentBlock(&environment, primary.get(), TRUE))
            {
                const DWORD environmentError = GetLastError();
                if (start.lpAttributeList) DeleteProcThreadAttributeList(start.lpAttributeList);
                Fail("CreateEnvironmentBlock(installer account)", environmentError);
            }
            created = CreateProcessAsUserW(primary.get(), executable.c_str(), command.data(), nullptr, nullptr,
                FALSE, flags | CREATE_UNICODE_ENVIRONMENT, environment, nullptr, &start.StartupInfo, &child);
        }
        else if (GetLastError() == ERROR_NO_TOKEN)
        {
            created = CreateProcessW(executable.c_str(), command.data(), nullptr, nullptr, FALSE,
                flags, nullptr, nullptr, &start.StartupInfo, &child);
        }
        else
        {
            const DWORD tokenError = GetLastError();
            if (start.lpAttributeList) DeleteProcThreadAttributeList(start.lpAttributeList);
            Fail("OpenThreadToken(installer account)", tokenError);
        }
        const DWORD error = GetLastError();
        if (environment) DestroyEnvironmentBlock(environment);
        if (start.lpAttributeList) DeleteProcThreadAttributeList(start.lpAttributeList);
        if (!created) Fail("CreateProcess(installer helper)", error);
        Handle process(child.hProcess);
        Handle thread(child.hThread);
        // Preparation owns its authenticated shutdown deadline and upload guard.
        // Never terminate it, its host, or a process tree to finish installation.
        if (WaitForSingleObject(process.get(), INFINITE) != WAIT_OBJECT_0)
            Fail("WaitForSingleObject(installer helper)");
        DWORD exitCode = 0;
        if (!GetExitCodeProcess(process.get(), &exitCode)) Fail("GetExitCodeProcess");
        return exitCode;
    }

    class ExtractedPayload
    {
        std::wstring directory;
        std::wstring executable;
        Handle directoryHandle;
        Handle fileHandle;
        std::vector<Handle> ancestorHandles;
        bool directoryVerified = false;
    public:
        ExtractedPayload(WORD resourceId, const wchar_t* name)
        {
            try { Extract(resourceId, name); }
            catch (...) { Cleanup(); throw; }
        }
        ~ExtractedPayload() { Cleanup(); }
        const std::wstring& path() const { return executable; }
        std::wstring diagnosticPath() const { return directory + L"\\cet-policy-error.txt"; }
    private:
        void Cleanup()
        {
            fileHandle.reset();
            // Do not traverse a directory that failed its retained-handle check.
            if (directoryVerified)
            {
                if (!executable.empty()) DeleteFileW(executable.c_str());
                DeleteFileW(diagnosticPath().c_str());
            }
            directoryHandle.reset();
            if (!directory.empty()) RemoveDirectoryW(directory.c_str());
        }

        void Extract(WORD resourceId, const wchar_t* name)
        {
            HANDLE rawToken = nullptr;
            if (!OpenThreadToken(GetCurrentThread(), TOKEN_QUERY, TRUE, &rawToken))
            {
                if (GetLastError() != ERROR_NO_TOKEN) Fail("OpenThreadToken");
                if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &rawToken)) Fail("OpenProcessToken");
            }
            Handle token(rawToken);
            DWORD tokenSize = 0;
            GetTokenInformation(token.get(), TokenUser, nullptr, 0, &tokenSize);
            if (!tokenSize) Fail("GetTokenInformation(size)");
            std::vector<BYTE> tokenBuffer(tokenSize);
            if (!GetTokenInformation(token.get(), TokenUser, tokenBuffer.data(), tokenSize, &tokenSize))
                Fail("GetTokenInformation");
            LPWSTR sidText = nullptr;
            if (!ConvertSidToStringSidW(reinterpret_cast<TOKEN_USER*>(tokenBuffer.data())->User.Sid, &sidText))
                Fail("ConvertSidToStringSid");
            const std::wstring sddl = L"D:P(A;OICI;FA;;;SY)(A;OICI;FA;;;" + std::wstring(sidText) + L")";
            LocalFree(sidText);
            PSECURITY_DESCRIPTOR descriptor = nullptr;
            if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl.c_str(), SDDL_REVISION_1,
                &descriptor, nullptr)) Fail("ConvertStringSecurityDescriptor");
            SECURITY_ATTRIBUTES security{ sizeof(SECURITY_ATTRIBUTES), descriptor, FALSE };
            std::vector<wchar_t> windows(32768);
            const UINT windowsSize = GetSystemWindowsDirectoryW(windows.data(), static_cast<UINT>(windows.size()));
            if (!windowsSize || windowsSize >= windows.size())
            {
                const DWORD error = GetLastError();
                LocalFree(descriptor);
                Fail("GetSystemWindowsDirectory", error);
            }
            const std::wstring temp = AbsolutePath(std::wstring(windows.data(), windowsSize) + L"\\Temp");
            try { PinAncestors(temp); }
            catch (...) { LocalFree(descriptor); throw; }
            GUID id{};
            if (FAILED(CoCreateGuid(&id)))
            {
                LocalFree(descriptor);
                throw std::runtime_error("Could not create a unique installer directory");
            }
            wchar_t guid[40]{};
            StringFromGUID2(id, guid, static_cast<int>(std::size(guid)));
            const std::wstring candidate = temp + L"\\ResoDrive-Installation-" + guid;
            const BOOL created = CreateDirectoryW(candidate.c_str(), &security);
            const DWORD directoryError = GetLastError();
            LocalFree(descriptor);
            if (!created) Fail("CreateDirectory(private installer helper)", directoryError);
            directory = candidate;
            directoryHandle.reset(CreateFileW(directory.c_str(), FILE_TRAVERSE | FILE_READ_ATTRIBUTES,
                FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr, OPEN_EXISTING,
                FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT, nullptr));
            if (!directoryHandle.valid()) Fail("Open private installer directory");
            BY_HANDLE_FILE_INFORMATION information{};
            if (!GetFileInformationByHandle(directoryHandle.get(), &information))
                Fail("Inspect private installer directory");
            if (!(information.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) ||
                (information.dwFileAttributes & FILE_ATTRIBUTE_REPARSE_POINT))
                throw std::runtime_error("The installer directory is not a regular directory");
            directoryVerified = true;
            executable = directory + L"\\" + name;
            const HRSRC resource = FindResourceW(module, MAKEINTRESOURCEW(resourceId), RT_RCDATA);
            if (!resource) Fail("FindResource(application)");
            const DWORD resourceSize = SizeofResource(module, resource);
            const HGLOBAL loaded = LoadResource(module, resource);
            const void* bytes = loaded ? LockResource(loaded) : nullptr;
            if (!bytes || !resourceSize) Fail("LoadResource(application)");
            Handle output(CreateFileW(executable.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_NEW,
                FILE_ATTRIBUTE_TEMPORARY, nullptr));
            if (!output.valid()) Fail("CreateFile(installer helper)");
            DWORD written = 0;
            if (!WriteFile(output.get(), bytes, resourceSize, &written, nullptr) || written != resourceSize)
                Fail("WriteFile(installer helper)");
            if (!FlushFileBuffers(output.get())) Fail("FlushFileBuffers(installer helper)");
            output.reset();
#ifdef RESODRIVE_INSTALLER_ACTIONS_TESTING
            if (AfterPayloadWrite) AfterPayloadWrite(executable);
#endif
            // Keep the exact embedded image immutable while Windows executes it.
            fileHandle.reset(CreateFileW(executable.c_str(), GENERIC_READ | FILE_EXECUTE, FILE_SHARE_READ, nullptr,
                OPEN_EXISTING, FILE_FLAG_OPEN_REPARSE_POINT, nullptr));
            if (!fileHandle.valid()) Fail("Retain installer helper image");
            VerifyPayload(fileHandle.get(), bytes, resourceSize);
        }

        void PinAncestors(const std::wstring& base)
        {
            if (base.size() < 3 || base[1] != L':' || base[2] != L'\\')
                throw std::runtime_error("The Windows temporary directory must be on a local drive");
            size_t end = 3;
            for (;;)
            {
                const auto path = base.substr(0, end);
                Handle ancestor(CreateFileW(path.c_str(), FILE_TRAVERSE | FILE_READ_ATTRIBUTES, FILE_SHARE_READ | FILE_SHARE_WRITE,
                    nullptr, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT, nullptr));
                if (!ancestor.valid()) Fail("Pin Windows temporary directory ancestor");
                BY_HANDLE_FILE_INFORMATION information{};
                if (!GetFileInformationByHandle(ancestor.get(), &information))
                    Fail("Inspect Windows temporary directory ancestor");
                if (!(information.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) ||
                    (information.dwFileAttributes & FILE_ATTRIBUTE_REPARSE_POINT))
                    throw std::runtime_error("A Windows temporary directory ancestor is not a regular directory");
                ancestorHandles.push_back(std::move(ancestor));
                if (end == base.size()) break;
                end = base.find(L'\\', end == 3 ? 3 : end + 1);
                if (end == std::wstring::npos) end = base.size();
            }
        }

        void VerifyPayload(HANDLE file, const void* expected, DWORD length)
        {
            BY_HANDLE_FILE_INFORMATION information{};
            if (!GetFileInformationByHandle(file, &information)) Fail("Inspect retained installer payload");
            if (information.dwFileAttributes & (FILE_ATTRIBUTE_REPARSE_POINT | FILE_ATTRIBUTE_DIRECTORY))
                throw std::runtime_error("The installer payload is not a regular file");
            if (information.nFileSizeHigh != 0 || information.nFileSizeLow != length)
                throw std::runtime_error("The installer payload length differs from its embedded resource");
            BYTE buffer[65536];
            const BYTE* resource = static_cast<const BYTE*>(expected);
            DWORD offset = 0;
            while (offset < length)
            {
                const DWORD requested = (std::min)(static_cast<DWORD>(sizeof(buffer)), length - offset);
                DWORD read = 0;
                if (!ReadFile(file, buffer, requested, &read, nullptr)) Fail("Read retained installer payload");
                if (read != requested || std::memcmp(buffer, resource + offset, read) != 0)
                    throw std::runtime_error("The installer payload differs from its embedded resource");
                offset += read;
            }
        }
    };

    template<typename Action> UINT Execute(MSIHANDLE installation, Action action)
    {
        try
        {
            const DWORD exitCode = action();
            Log(installation, L"ResoDrive installer helper returned " + std::to_wstring(exitCode) + L".");
            return exitCode == 0 ? ERROR_SUCCESS : ERROR_INSTALL_FAILURE;
        }
        catch (const std::exception& exception)
        {
            const std::string message(exception.what());
            Log(installation, L"ResoDrive installer helper failed: " + std::wstring(message.begin(), message.end()));
            return ERROR_INSTALL_FAILURE;
        }
        catch (...)
        {
            Log(installation, L"ResoDrive installer helper failed unexpectedly.");
            return ERROR_INSTALL_FAILURE;
        }
    }

    DWORD Embedded(const std::vector<std::wstring>& arguments, bool optOut, bool interactive = false)
    {
        ExtractedPayload application(ApplicationResource, L"resodrive.exe");
        return Run(application.path(), arguments, optOut, interactive);
    }

    UINT SimpleMigration(MSIHANDLE installation, const wchar_t* argument)
    {
        return Execute(installation, [&] {
            return Embedded({ argument }, OptOut(Property(installation, L"CustomActionData")));
        });
    }

    void LogPolicyDiagnostic(MSIHANDLE installation, const std::wstring& path)
    {
        Handle input(CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING,
            FILE_FLAG_OPEN_REPARSE_POINT, nullptr));
        if (!input.valid()) return;
        BY_HANDLE_FILE_INFORMATION information{};
        if (!GetFileInformationByHandle(input.get(), &information) ||
            (information.dwFileAttributes & (FILE_ATTRIBUTE_REPARSE_POINT | FILE_ATTRIBUTE_DIRECTORY))) return;
        char bytes[4096]{};
        DWORD read = 0;
        if (!ReadFile(input.get(), bytes, sizeof(bytes), &read, nullptr) || !read) return;
        int count = static_cast<int>(read);
        int characters = 0;
        // A bounded read can split the last UTF-8 character. Discard at most
        // those trailing bytes; malformed diagnostic text is never reinterpreted.
        for (int trimmed = 0; trimmed < 4 && count > 0; ++trimmed, --count)
        {
            characters = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, bytes, count, nullptr, 0);
            if (characters) break;
        }
        if (!characters) return;
        std::wstring message(static_cast<size_t>(characters), L'\0');
        if (!MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, bytes, count, message.data(), characters)) return;
        for (auto& ch : message)
            if (ch < 32 && ch != L'\r' && ch != L'\n' && ch != L'\t') ch = L' ';
        if (!message.empty() && message.front() == L'\ufeff') message.erase(0, 1);
        Log(installation, L"ResoDrive CET policy failed: " + message);
    }

    UINT Policy(MSIHANDLE installation, const wchar_t* action)
    {
        return Execute(installation, [&] {
            const auto data = Property(installation, L"CustomActionData");
            const size_t split = data.find(L'\n');
            if (split == std::wstring::npos) throw std::runtime_error("CET policy action data is invalid");
            const auto mode = data.substr(0, split);
            (void)OptOut(mode);
            const auto target = AbsolutePath(data.substr(split + 1));
            ExtractedPayload script(PolicyResource, L"CetPolicy.ps1");
            std::vector<wchar_t> system(32768);
            const UINT size = GetSystemDirectoryW(system.data(), static_cast<UINT>(system.size()));
            if (!size || size >= system.size()) Fail("GetSystemDirectory");
            const std::wstring powershell = std::wstring(system.data(), size) +
                L"\\WindowsPowerShell\\v1.0\\powershell.exe";
            const auto diagnostic = script.diagnosticPath();
            const DWORD exitCode = Run(powershell, { L"-NoLogo", L"-NoProfile", L"-NonInteractive", L"-ExecutionPolicy", L"Bypass",
                L"-File", script.path(), L"-Action", action, L"-ExecutablePath", target, L"-DisableCet", mode,
                L"-DiagnosticPath", diagnostic }, false);
            if (exitCode != 0) LogPolicyDiagnostic(installation, diagnostic);
            return exitCode;
        });
    }
}

BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH) module = instance;
    return TRUE;
}

extern "C" UINT __stdcall PrepareRegisteredInstallation(MSIHANDLE installation)
{
    return Execute(installation, [&] {
        const auto uiLevel = Property(installation, L"UILevel");
        const bool interactive = uiLevel == L"3" || uiLevel == L"4" || uiLevel == L"5";
        return Embedded({ L"--prepare-registered-install", AbsolutePath(Property(installation, L"INSTALLFOLDER")),
            uiLevel, AbsolutePath(Property(installation, L"RDRIVE_DATA_ROOT")) },
            OptOut(Property(installation, L"RDRIVE_HELPER_DISABLE_CET")), interactive);
    });
}

extern "C" UINT __stdcall StageMigration(MSIHANDLE installation)
{
    return Execute(installation, [&] {
        const auto data = Property(installation, L"CustomActionData");
        const size_t split = data.find(L'\n');
        if (split == std::wstring::npos) throw std::runtime_error("StageMigration data is invalid");
        const bool optOut = OptOut(data.substr(0, split));
        return Embedded({ L"--stage-install-migration", AbsolutePath(data.substr(split + 1)) }, optOut);
    });
}

extern "C" UINT __stdcall ApplyMigration(MSIHANDLE installation)
{
    return SimpleMigration(installation, L"--apply-install-migration");
}

extern "C" UINT __stdcall RollbackMigration(MSIHANDLE installation)
{
    return SimpleMigration(installation, L"--rollback-install-migration");
}

extern "C" UINT __stdcall CommitMigration(MSIHANDLE installation)
{
    return SimpleMigration(installation, L"--commit-install-migration");
}

extern "C" UINT __stdcall UnregisterAutostart(MSIHANDLE installation)
{
    return Execute(installation, [&] {
        std::wstring directory = AbsolutePath(Property(installation, L"INSTALLFOLDER"));
        if (directory.back() != L'\\') directory += L'\\';
        return Run(directory + L"resodrive.exe", { L"--unregister-autostart" },
            OptOut(Property(installation, L"RDRIVE_HELPER_DISABLE_CET")));
    });
}

extern "C" UINT __stdcall ConfigureCetPolicy(MSIHANDLE installation)
{
    return Policy(installation, L"Apply");
}

extern "C" UINT __stdcall RollbackCetPolicy(MSIHANDLE installation)
{
    return Policy(installation, L"Rollback");
}

extern "C" UINT __stdcall CommitCetPolicy(MSIHANDLE installation)
{
    return Policy(installation, L"Commit");
}

extern "C" UINT __stdcall RemoveCetPolicy(MSIHANDLE installation)
{
    return Policy(installation, L"Remove");
}
