#define RESODRIVE_INSTALLER_ACTIONS_TESTING
#include "InstallerActions.cpp"
#include <iostream>

namespace
{
    const std::wstring ForwardedArgument = L"space \\ \"quoted\" trailing\\ unicode \u03a9";

    bool ShadowStacksEnabled()
    {
        PROCESS_MITIGATION_USER_SHADOW_STACK_POLICY policy{};
        if (!GetProcessMitigationPolicy(GetCurrentProcess(), ProcessUserShadowStackPolicy, &policy, sizeof(policy)))
            Fail("GetProcessMitigationPolicy(test child)");
        return policy.EnableUserShadowStack != 0;
    }

    void Require(bool condition, const char* message)
    {
        if (!condition) throw std::runtime_error(message);
    }

    std::wstring AccountSid()
    {
        HANDLE raw = nullptr;
        if (!OpenThreadToken(GetCurrentThread(), TOKEN_QUERY, TRUE, &raw))
        {
            if (GetLastError() != ERROR_NO_TOKEN) Fail("OpenThreadToken(test)");
            if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &raw)) Fail("OpenProcessToken(test)");
        }
        Handle token(raw);
        DWORD size = 0;
        GetTokenInformation(token.get(), TokenUser, nullptr, 0, &size);
        std::vector<BYTE> buffer(size);
        if (!GetTokenInformation(token.get(), TokenUser, buffer.data(), size, &size))
            Fail("GetTokenInformation(test)");
        LPWSTR text = nullptr;
        if (!ConvertSidToStringSidW(reinterpret_cast<TOKEN_USER*>(buffer.data())->User.Sid, &text))
            Fail("ConvertSidToStringSid(test)");
        const std::wstring result(text);
        LocalFree(text);
        return result;
    }

    class ScopedImpersonation
    {
    public:
        ScopedImpersonation()
        {
            HANDLE raw = nullptr;
            if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY | TOKEN_DUPLICATE, &raw))
                Fail("OpenProcessToken(impersonation test)");
            Handle token(raw);
            if (!ImpersonateLoggedOnUser(token.get())) Fail("ImpersonateLoggedOnUser(test)");
        }
        ~ScopedImpersonation() { RevertToSelf(); }
    };

    std::wstring tamperedPath;

    void CorruptPayload(const std::wstring& path)
    {
        tamperedPath = path;
        Handle output(CreateFileW(path.c_str(), GENERIC_WRITE, 0, nullptr, OPEN_EXISTING, 0, nullptr));
        if (!output.valid()) Fail("Open extraction gap test fixture");
        const char corrupt = '!';
        DWORD written = 0;
        if (!WriteFile(output.get(), &corrupt, 1, &written, nullptr) || written != 1)
            Fail("Corrupt extraction gap test fixture");
    }

    void TruncatePayload(const std::wstring& path)
    {
        tamperedPath = path;
        Handle output(CreateFileW(path.c_str(), GENERIC_WRITE, 0, nullptr, TRUNCATE_EXISTING, 0, nullptr));
        if (!output.valid()) Fail("Truncate extraction gap test fixture");
    }

    void ReplaceWithDirectory(const std::wstring& path)
    {
        tamperedPath = path;
        if (!DeleteFileW(path.c_str()) || !CreateDirectoryW(path.c_str(), nullptr))
            Fail("Replace extraction gap test fixture");
    }

    void RequireRejectedPayload(void (*hook)(const std::wstring&))
    {
        AfterPayloadWrite = hook;
        bool rejected = false;
        try { ExtractedPayload application(ApplicationResource, L"resodrive.exe"); }
        catch (const std::exception&) { rejected = true; }
        AfterPayloadWrite = nullptr;
        Require(rejected, "An altered extracted payload was accepted");
        // A replaced directory is deliberately not recursively deleted by the
        // wrapper. Remove only our known empty fixture and its private parent.
        if (GetFileAttributesW(tamperedPath.c_str()) != INVALID_FILE_ATTRIBUTES)
        {
            Require(RemoveDirectoryW(tamperedPath.c_str()) != FALSE, "Could not remove directory fixture");
            const auto parent = tamperedPath.substr(0, tamperedPath.find_last_of(L'\\'));
            Require(RemoveDirectoryW(parent.c_str()) != FALSE, "Could not remove private fixture parent");
        }
    }
}

int wmain(int count, wchar_t** arguments)
{
    try
    {
        if (count == 5 && std::wstring(arguments[1]) == L"--child")
        {
            const bool expected = OptOut(arguments[2]);
            Require(ShadowStacksEnabled() == expected, "Child mitigation differs from expected state");
            Require(arguments[3] == ForwardedArgument, "Arguments were not forwarded exactly");
            Require(arguments[4] == AccountSid(), "The child was launched under another Windows account");
            return 0;
        }
        Require(count == 1, "Unexpected test arguments");
        std::vector<wchar_t> image(32768);
        const DWORD length = GetModuleFileNameW(nullptr, image.data(), static_cast<DWORD>(image.size()));
        if (!length || length >= image.size()) Fail("GetModuleFileName(test)");
        const std::wstring executable(image.data(), length);
        const bool enabled = ShadowStacksEnabled();
        const auto account = AccountSid();
        Require(Run(executable, { L"--child", enabled ? L"1" : L"0", ForwardedArgument, account }, false) == 0,
            "Normal process creation changed CET or arguments");
        Require(Run(executable, { L"--child", L"0", ForwardedArgument, account }, true) == 0,
            "Opt-out was not applied before the child began executing");
        {
            ScopedImpersonation impersonation;
            Require(Run(executable, { L"--child", L"0", ForwardedArgument, account }, true) == 0,
                "The impersonated installer account could not launch its helper");
            Require(Run(executable, { L"--child", L"0", ForwardedArgument, account }, true, true) == 0,
                "The impersonated account could not launch on its interactive desktop");
        }
        // The resource is the published application, not a second compatibility
        // variant. Its read-only inspection verb does not launch a host or migrate.
        module = GetModuleHandleW(nullptr);
        RequireRejectedPayload(CorruptPayload);
        RequireRejectedPayload(TruncatePayload);
        RequireRejectedPayload(ReplaceWithDirectory);
        std::wstring extracted;
        {
            ExtractedPayload application(ApplicationResource, L"resodrive.exe");
            extracted = application.path();
            std::vector<wchar_t> systemWindows(32768);
            const UINT windowsLength = GetSystemWindowsDirectoryW(systemWindows.data(), static_cast<UINT>(systemWindows.size()));
            if (!windowsLength || windowsLength >= systemWindows.size()) Fail("GetSystemWindowsDirectory(test)");
            const std::wstring expectedBase = std::wstring(systemWindows.data(), windowsLength) + L"\\Temp\\";
            Require(extracted.starts_with(expectedBase), "Extraction used an environment-controlled temporary directory");
            const auto privateDirectory = extracted.substr(0, extracted.find_last_of(L'\\'));
            Handle remove(CreateFileW(privateDirectory.c_str(), DELETE, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                nullptr, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS, nullptr));
            Require(!remove.valid(), "The private payload directory could be removed while retained");
            Handle mutation(CreateFileW(extracted.c_str(), GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE,
                nullptr, OPEN_EXISTING, 0, nullptr));
            Require(!mutation.valid(), "An executing embedded payload could be modified");
            Require(Run(extracted, { L"--inspect-installation" }, true) == 0,
                "The embedded managed application failed its read-only inspection");
        }
        Require(GetFileAttributesW(extracted.c_str()) == INVALID_FILE_ATTRIBUTES,
            "The extracted application was not cleaned up");
        std::wstring diagnostic;
        {
            // Rejecting a relative target happens before opening policy state;
            // this checks the real embedded error path without changing policy.
            ExtractedPayload script(PolicyResource, L"CetPolicy.ps1");
            diagnostic = script.diagnosticPath();
            std::vector<wchar_t> system(32768);
            const UINT systemLength = GetSystemDirectoryW(system.data(), static_cast<UINT>(system.size()));
            if (!systemLength || systemLength >= system.size()) Fail("GetSystemDirectory(test)");
            const std::wstring powershell = std::wstring(system.data(), systemLength) +
                L"\\WindowsPowerShell\\v1.0\\powershell.exe";
            const DWORD exitCode = Run(powershell, { L"-NoLogo", L"-NoProfile", L"-NonInteractive",
                L"-ExecutionPolicy", L"Bypass", L"-File", script.path(), L"-Action", L"Apply",
                L"-ExecutablePath", L"relative", L"-DisableCet", L"0", L"-DiagnosticPath", diagnostic }, false);
            Require(exitCode != 0, "The policy script accepted a relative target");
            Handle report(CreateFileW(diagnostic.c_str(), GENERIC_READ, FILE_SHARE_READ, nullptr,
                OPEN_EXISTING, FILE_FLAG_OPEN_REPARSE_POINT, nullptr));
            Require(report.valid(), "A policy failure did not preserve its diagnostic");
            LARGE_INTEGER reportSize{};
            Require(GetFileSizeEx(report.get(), &reportSize) && reportSize.QuadPart > 0 && reportSize.QuadPart <= 4096,
                "The policy diagnostic was empty or unbounded");
        }
        Require(GetFileAttributesW(diagnostic.c_str()) == INVALID_FILE_ATTRIBUTES,
            "The private policy diagnostic was not cleaned up");
        std::wcout << L"Native installer checks passed. Default shadow stacks: " << (enabled ? L"on" : L"off")
            << L"; opt-out child: off; account/arguments preserved; embedded application and policy diagnostics cleaned up.\n";
        return 0;
    }
    catch (const std::exception& exception)
    {
        std::cerr << exception.what() << '\n';
        return 1;
    }
}
