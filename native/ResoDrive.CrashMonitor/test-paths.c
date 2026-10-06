// Production helper coverage without policy changes, application launch or crash.
#define wWinMain monitor_entry_not_invoked_by_test
#include "monitor.c"

int wmain(int count, wchar_t **arguments) {
    if (count != 5) return 2;
    // Paths are created by the smoke script exclusively inside its disposable root.
    if (!safe_directory(arguments[1], FALSE) || safe_directory(arguments[2], FALSE) ||
        safe_directory(arguments[3], FALSE)) return 3;
    wchar_t path[PATH_CAP], summary[2048];
    if (!join_path(path, PATH_CAP, arguments[2], L"must-not-be-created") || safe_directory(path, TRUE)) return 4;
    StringCchCopyW(incident.directory, PATH_CAP, arguments[2]);
    if (!join_path(incident.executable, PATH_CAP, arguments[2], L"resodrive.exe")) return 5;
    executable_metadata();
    if (wcsstr(incident.hash, L"not read") == NULL || wcscmp(incident.version, L"unavailable") != 0) return 6;
    StringCchCopyW(incident.directory, PATH_CAP, arguments[1]);
    StringCchCopyW(incident.executable, PATH_CAP, arguments[4]);
    executable_metadata();
    if (wcslen(incident.hash) != 64) return 7;
    incident.exitCode = 0x80131506;
    incident.pid = 123;
    incident.role = L"ui";
    StringCchCopyW(incident.utc, ARRAYSIZE(incident.utc), L"2026-10-06T00:00:00.000Z");
    StringCchCopyW(incident.os, ARRAYSIZE(incident.os), L"Windows 10.0 build 19043 (x64)");
    StringCchCopyW(incident.reference, ARRAYSIZE(incident.reference), L"disposable-fallback-reference");
    for (unsigned i = 0; i < PATH_CAP - 1; ++i) incident.report[i] = L'a';
    incident.report[PATH_CAP - 1] = L'\0';
    fallback_summary(summary, ARRAYSIZE(summary), L"ResoDrive closed unexpectedly");
    if (!wcsstr(summary, L"80131506") || !wcsstr(summary, L"disposable-fallback-reference") ||
        !wcsstr(summary, L"path shortened") || !wcsstr(summary, L"Ctrl+C") || !wcsstr(summary, incident.hash)) return 8;
    incident.report[0] = L'\0';
    incident.recordingError = ERROR_ACCESS_DENIED;
    fallback_summary(summary, ARRAYSIZE(summary), L"ResoDrive closed unexpectedly");
    if (!wcsstr(summary, L"unavailable") || !wcsstr(summary, L"Ctrl+C") || !wcsstr(summary, L"Windows status: 5")) return 9;
    StringCchCopyW(incident.details, TEXT_CAP, L"fixture-complete-summary-tail");
    display_details(summary, 1024);
    if (wcslen(summary) >= 1024 || !wcsstr(summary, incident.utc) || !wcsstr(summary, incident.os) ||
        !wcsstr(summary, L"80131506") || !wcsstr(summary, L"Copy details") || !wcsstr(summary, L"storage is unavailable") ||
        wcsstr(summary, L"fixture-complete-summary-tail")) return 10;
    return 0;
}
