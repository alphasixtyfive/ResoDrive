// Disposable smoke-test executable. Never packaged with ResoDrive.
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <shellapi.h>
#include <werapi.h>
#include <strsafe.h>
#include <stdlib.h>
#include <wchar.h>

int WINAPI wWinMain(HINSTANCE instance, HINSTANCE previous, PWSTR commandLine, int show) {
    int count = 0;
    wchar_t **arguments = CommandLineToArgvW(GetCommandLineW(), &count);
    DWORD code = 0;
    (void)instance; (void)previous; (void)commandLine; (void)show;
    for (int i = 1; arguments && i < count; ++i) {
        if (wcscmp(arguments[i], L"--identity") == 0 && i + 1 < count) {
            HANDLE file = CreateFileW(arguments[++i], GENERIC_WRITE, 0, NULL, CREATE_NEW, FILE_FLAG_WRITE_THROUGH, NULL);
            FILETIME created, exited, kernel, user;
            if (file != INVALID_HANDLE_VALUE && GetProcessTimes(GetCurrentProcess(), &created, &exited, &kernel, &user)) {
                ULARGE_INTEGER time;
                char text[128];
                DWORD written;
                time.LowPart = created.dwLowDateTime; time.HighPart = created.dwHighDateTime;
                StringCchPrintfA(text, sizeof(text), "%lu\n%llu\n", GetCurrentProcessId(), time.QuadPart);
                WriteFile(file, text, (DWORD)strlen(text), &written, NULL);
                FlushFileBuffers(file);
            }
            if (file != INVALID_HANDLE_VALUE) CloseHandle(file);
        }
        else if (wcscmp(arguments[i], L"--exit") == 0 && i + 1 < count)
            code = wcstoul(arguments[++i], NULL, 0);
        else if (wcscmp(arguments[i], L"--sleep") == 0 && i + 1 < count)
            Sleep(wcstoul(arguments[++i], NULL, 10));
        else if (wcscmp(arguments[i], L"--wait-trigger") == 0 && i + 1 < count) {
            const wchar_t *trigger = arguments[++i];
            ULONGLONG deadline = GetTickCount64() + 30000;
            while (GetFileAttributesW(trigger) == INVALID_FILE_ATTRIBUTES) {
                if (GetTickCount64() >= deadline) ExitProcess(4);
                Sleep(10);
            }
        }
        else if (wcscmp(arguments[i], L"--fail-fast") == 0) {
            // Real unhandled fatal exception: opt-in WER acceptance on hosted CI only.
            (void)WerSetFlags(WER_FAULT_REPORTING_NO_UI);
            RaiseFailFastException(NULL, NULL, 0);
        }
        else if (wcscmp(arguments[i], L"--record-args") == 0 && i + 1 < count) {
            HANDLE file = CreateFileW(arguments[++i], GENERIC_WRITE, 0, NULL, CREATE_NEW, FILE_ATTRIBUTE_NORMAL, NULL);
            if (file != INVALID_HANDLE_VALUE) {
                DWORD written;
                for (int j = i + 1; j < count; ++j) {
                    WriteFile(file, arguments[j], (DWORD)(wcslen(arguments[j]) * sizeof(wchar_t)), &written, NULL);
                    WriteFile(file, L"\n", sizeof(wchar_t), &written, NULL);
                }
                CloseHandle(file);
            }
            break;
        }
    }
    if (arguments) LocalFree(arguments);
    ExitProcess(code);
}
