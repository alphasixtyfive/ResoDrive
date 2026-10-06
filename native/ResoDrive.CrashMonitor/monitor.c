// Native crash observer: intentionally independent of .NET/WPF and never a debugger.
// It owns no application children, performs no restart, and writes metadata only.
#define WIN32_LEAN_AND_MEAN
#define _WIN32_WINNT 0x0A00
#define _CRT_SECURE_NO_WARNINGS
#include <windows.h>
#include <commctrl.h>
#include <shellapi.h>
#include <shlobj.h>
#include <bcrypt.h>
#include <strsafe.h>
#include <sddl.h>
#include <stdlib.h>
#include <wchar.h>
#include "event-evidence.h"

#define PATH_CAP 32768
#define TEXT_CAP 20480
#define MAX_SUMMARIES 32
#define COPY_BUTTON 1001
#define FOLDER_BUTTON 1002
#define FAILURE_ALREADY_DISPLAYED ((DWORD)0xE0524447)
#define ACTIVATION_TIMED_OUT ((DWORD)0xE0524448)

typedef struct {
    wchar_t executable[PATH_CAP];
    wchar_t directory[PATH_CAP];
    wchar_t diagnostics[PATH_CAP];
    wchar_t report[PATH_CAP];
    wchar_t details[TEXT_CAP];
    wchar_t version[256];
    wchar_t hash[128];
    wchar_t dump[PATH_CAP];
    wchar_t dumpPolicy[512];
    wchar_t dumpPreparation[512];
    wchar_t eventEvidence[4096];
    DWORD inheritedErrorMode;
    DWORD observerErrorMode;
    DWORD pid;
    FILETIME creation;
    DWORD exitCode;
    DWORD recordingError;
    const wchar_t *role;
    BOOL noDialog;
    BOOL sessionEnding;
    BOOL launchFailed;
} Incident;

static Incident incident;

static ULONGLONG filetime_number(FILETIME time) {
    ULARGE_INTEGER value;
    value.LowPart = time.dwLowDateTime;
    value.HighPart = time.dwHighDateTime;
    return value.QuadPart;
}

static BOOL join_path(wchar_t *result, size_t capacity, const wchar_t *directory, const wchar_t *name) {
    return SUCCEEDED(StringCchPrintfW(result, capacity, L"%s\\%s", directory, name));
}

static BOOL local_filesystem_path(const wchar_t *path) {
    // Crash-critical I/O must not wait on disconnected shares or mapped drives.
    if (wcsncmp(path, L"\\\\?\\", 4) == 0) path += 4;
    if (wcslen(path) < 3 || path[1] != L':' || (path[2] != L'\\' && path[2] != L'/')) {
        SetLastError(ERROR_NOT_SUPPORTED); return FALSE;
    }
    wchar_t drive[4] = {path[0], L':', L'\\', L'\0'};
    UINT type = GetDriveTypeW(drive);
    if (type == DRIVE_REMOTE || type == DRIVE_UNKNOWN || type == DRIVE_NO_ROOT_DIR) {
        SetLastError(ERROR_NOT_SUPPORTED); return FALSE;
    }
    return TRUE;
}

static BOOL adjacent_application(void) {
    DWORD length = GetModuleFileNameW(NULL, incident.directory, PATH_CAP);
    wchar_t *last;
    if (!length || length >= PATH_CAP) { SetLastError(ERROR_FILENAME_EXCED_RANGE); return FALSE; }
    last = wcsrchr(incident.directory, L'\\');
    if (!last) { SetLastError(ERROR_BAD_PATHNAME); return FALSE; }
    *last = L'\0';
    if (!join_path(incident.executable, PATH_CAP, incident.directory, L"resodrive.exe")) {
        SetLastError(ERROR_FILENAME_EXCED_RANGE); return FALSE;
    }
    return TRUE;
}

// Resolve relative RDRIVE_DATA_DIR against the app directory, exactly as managed paths do.
// Metadata goes alongside, never inside, the account data root.
static BOOL diagnostics_path(void) {
    wchar_t configured[PATH_CAP], root[PATH_CAP], combined[PATH_CAP];
    DWORD count = GetEnvironmentVariableW(L"RDRIVE_DATA_DIR", configured, PATH_CAP);
    if (count >= PATH_CAP) { SetLastError(ERROR_FILENAME_EXCED_RANGE); return FALSE; }
    if (count) {
        if ((configured[0] == L'\\' && configured[1] == L'\\') ||
            (wcslen(configured) >= 3 && configured[1] == L':' && (configured[2] == L'\\' || configured[2] == L'/'))) {
            if (FAILED(StringCchCopyW(combined, PATH_CAP, configured))) return FALSE;
        } else if (!join_path(combined, PATH_CAP, incident.directory, configured)) return FALSE;
        count = GetFullPathNameW(combined, PATH_CAP, root, NULL);
        if (!count || count >= PATH_CAP) return FALSE;
    } else {
        wchar_t *local = NULL;
        HRESULT result = SHGetKnownFolderPath(&FOLDERID_LocalAppData, KF_FLAG_DONT_VERIFY, NULL, &local);
        if (FAILED(result)) { SetLastError(ERROR_PATH_NOT_FOUND); return FALSE; }
        BOOL copied = join_path(root, PATH_CAP, local, L"rdrive");
        CoTaskMemFree(local);
        if (!copied) return FALSE;
    }
    // Path.GetFullPath can retain a final separator; normalize before appending suffix.
    size_t length = wcslen(root);
    while (length > 3 && (root[length - 1] == L'\\' || root[length - 1] == L'/')) root[--length] = L'\0';
    if (length <= 3) { SetLastError(ERROR_BAD_PATHNAME); return FALSE; }
    if (FAILED(StringCchPrintfW(incident.diagnostics, PATH_CAP, L"%s-diagnostics", root))) {
        SetLastError(ERROR_FILENAME_EXCED_RANGE); return FALSE;
    }
    return TRUE;
}

// Walk every existing component, refusing junctions/symlinks rather than writing through them.
// CREATE_NEW and a non-following handle also protect the individual report file.
static BOOL safe_directory(const wchar_t *path) {
    wchar_t prefix[PATH_CAP];
    size_t length = wcslen(path), start = 3;
    if (!local_filesystem_path(path)) return FALSE;
    if (length >= PATH_CAP || length < 3) { SetLastError(ERROR_BAD_PATHNAME); return FALSE; }
    if (wcsncmp(path, L"\\\\?\\", 4) == 0) start = 7;
    else if (path[0] == L'\\' && path[1] == L'\\') {
        const wchar_t *server = wcschr(path + 2, L'\\');
        const wchar_t *share = server ? wcschr(server + 1, L'\\') : NULL;
        if (!share) { SetLastError(ERROR_BAD_PATHNAME); return FALSE; }
        start = (size_t)(share - path) + 1;
    }
    for (size_t index = start; index <= length; ++index) {
        if (index != length && path[index] != L'\\' && path[index] != L'/') continue;
        if (FAILED(StringCchCopyNW(prefix, PATH_CAP, path, index))) return FALSE;
        DWORD attributes = GetFileAttributesW(prefix);
        if (attributes == INVALID_FILE_ATTRIBUTES) {
            DWORD error = GetLastError();
            if (error != ERROR_FILE_NOT_FOUND && error != ERROR_PATH_NOT_FOUND) return FALSE;
            if (!CreateDirectoryW(prefix, NULL) && GetLastError() != ERROR_ALREADY_EXISTS) return FALSE;
            attributes = GetFileAttributesW(prefix);
        }
        if (attributes == INVALID_FILE_ATTRIBUTES) return FALSE;
        if (!(attributes & FILE_ATTRIBUTE_DIRECTORY) || (attributes & FILE_ATTRIBUTE_REPARSE_POINT)) {
            SetLastError(ERROR_ACCESS_DENIED); return FALSE;
        }
    }
    return TRUE;
}

static void prepare_owned_dump_folder(void) {
    HKEY owner = NULL, policy = NULL;
    DWORD owned = 0, bytes = sizeof(owned), type = 0;
    wchar_t configured[PATH_CAP], expanded[PATH_CAP], expected[PATH_CAP];
    StringCchCopyW(incident.dumpPreparation, ARRAYSIZE(incident.dumpPreparation), L"No MSI-owned default WER folder was prepared; existing administrator policy was left untouched.");
    if (RegOpenKeyExW(HKEY_LOCAL_MACHINE, L"SOFTWARE\\ResoDrive\\Diagnostics", 0,
        KEY_QUERY_VALUE | KEY_WOW64_64KEY, &owner) != ERROR_SUCCESS) return;
    LSTATUS result = RegQueryValueExW(owner, L"LocalDumpsOwned", NULL, &type, (BYTE *)&owned, &bytes);
    RegCloseKey(owner);
    if (result != ERROR_SUCCESS || type != REG_DWORD || bytes != sizeof(owned) || owned != 1) return;
    if (RegOpenKeyExW(HKEY_LOCAL_MACHINE, L"SOFTWARE\\Microsoft\\Windows\\Windows Error Reporting\\LocalDumps\\resodrive.exe", 0,
        KEY_QUERY_VALUE | KEY_WOW64_64KEY, &policy) != ERROR_SUCCESS) return;
    bytes = sizeof(configured);
    result = RegQueryValueExW(policy, L"DumpFolder", NULL, &type, (BYTE *)configured, &bytes);
    RegCloseKey(policy);
    if (result != ERROR_SUCCESS || type != REG_EXPAND_SZ || bytes < sizeof(wchar_t) ||
        bytes > sizeof(configured) || configured[bytes / sizeof(wchar_t) - 1] != L'\0') return;
    DWORD expandedLength = ExpandEnvironmentStringsW(configured, expanded, PATH_CAP);
    if (!expandedLength || expandedLength > PATH_CAP) return;
    wchar_t *local = NULL;
    if (FAILED(SHGetKnownFolderPath(&FOLDERID_LocalAppData, KF_FLAG_DONT_VERIFY, NULL, &local))) return;
    BOOL copied = join_path(expected, PATH_CAP, local, L"rdrive-diagnostics\\dumps");
    CoTaskMemFree(local);
    if (!copied || _wcsicmp(expanded, expected) != 0) return;
    if (safe_directory(expected)) {
        StringCchCopyW(incident.dumpPreparation, ARRAYSIZE(incident.dumpPreparation), L"MSI-owned default WER folder exists before application execution. This does not guarantee capture.");
    } else {
        DWORD error = GetLastError();
        StringCchPrintfW(incident.dumpPreparation, ARRAYSIZE(incident.dumpPreparation), L"MSI-owned WER folder could not be prepared (Windows error %lu). Dump capture may be unavailable.", error);
    }
}

static void executable_metadata(void) {
    StringCchCopyW(incident.version, ARRAYSIZE(incident.version), L"unavailable");
    StringCchCopyW(incident.hash, ARRAYSIZE(incident.hash), L"unavailable");
    if (!local_filesystem_path(incident.executable)) {
        StringCchCopyW(incident.hash, ARRAYSIZE(incident.hash), L"unavailable (network executable was not read by the observer)");
        return;
    }
    DWORD unused = 0;
    DWORD bytes = GetFileVersionInfoSizeW(incident.executable, &unused);
    if (bytes && bytes <= 1024 * 1024) {
        void *info = HeapAlloc(GetProcessHeap(), 0, bytes);
        VS_FIXEDFILEINFO *fixed = NULL;
        UINT fixedSize = 0;
        if (info && GetFileVersionInfoW(incident.executable, 0, bytes, info) &&
            VerQueryValueW(info, L"\\", (void **)&fixed, &fixedSize) && fixedSize >= sizeof(*fixed)) {
            StringCchPrintfW(incident.version, ARRAYSIZE(incident.version), L"%u.%u.%u.%u",
                HIWORD(fixed->dwFileVersionMS), LOWORD(fixed->dwFileVersionMS),
                HIWORD(fixed->dwFileVersionLS), LOWORD(fixed->dwFileVersionLS));
        }
        if (info) HeapFree(GetProcessHeap(), 0, info);
    }
    BCRYPT_ALG_HANDLE algorithm = NULL;
    BCRYPT_HASH_HANDLE hash = NULL;
    HANDLE file = INVALID_HANDLE_VALUE;
    BYTE digest[32], buffer[65536];
    DWORD read = 0;
    BOOL succeeded = FALSE;
    if (BCryptOpenAlgorithmProvider(&algorithm, BCRYPT_SHA256_ALGORITHM, NULL, 0) < 0) goto done;
    if (BCryptCreateHash(algorithm, &hash, NULL, 0, NULL, 0, 0) < 0) goto done;
    file = CreateFileW(incident.executable, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_DELETE, NULL,
        OPEN_EXISTING, FILE_FLAG_SEQUENTIAL_SCAN, NULL);
    if (file == INVALID_HANDLE_VALUE) goto done;
    for (;;) {
        if (!ReadFile(file, buffer, sizeof(buffer), &read, NULL)) goto done;
        if (!read) break;
        if (BCryptHashData(hash, buffer, read, 0) < 0) goto done;
    }
    if (read || BCryptFinishHash(hash, digest, sizeof(digest), 0) < 0) goto done;
    for (size_t i = 0; i < sizeof(digest); ++i)
        StringCchPrintfW(incident.hash + i * 2, ARRAYSIZE(incident.hash) - i * 2, L"%02X", digest[i]);
    succeeded = TRUE;
done:
    if (!succeeded) StringCchCopyW(incident.hash, ARRAYSIZE(incident.hash), L"unavailable (file could not be hashed)");
    if (file != INVALID_HANDLE_VALUE) CloseHandle(file);
    if (hash) BCryptDestroyHash(hash);
    if (algorithm) BCryptCloseAlgorithmProvider(algorithm, 0);
}

static void system_error(DWORD code, wchar_t *text, size_t capacity) {
    if (!FormatMessageW(FORMAT_MESSAGE_FROM_SYSTEM | FORMAT_MESSAGE_IGNORE_INSERTS, NULL,
        code, 0, text, (DWORD)capacity, NULL)) StringCchPrintfW(text, capacity, L"Windows error %lu", code);
    size_t length = wcslen(text);
    while (length && (text[length - 1] == L'\r' || text[length - 1] == L'\n')) text[--length] = L'\0';
}

// WER owns capture. Observe its configured folder without creating it, opening memory
// dumps, changing policy, or associating an older dump with a reused process ID.
static void dump_evidence(void) {
    wchar_t folder[PATH_CAP], configured[PATH_CAP], filename[128], candidate[PATH_CAP];
    DWORD dumpType = 1;
    BOOL configuredPolicy = FALSE, folderConfigured = FALSE;
    StringCchCopyW(incident.dumpPolicy, ARRAYSIZE(incident.dumpPolicy), L"not configured by this observer");
    const wchar_t *keys[] = {
        L"SOFTWARE\\Microsoft\\Windows\\Windows Error Reporting\\LocalDumps",
        L"SOFTWARE\\Microsoft\\Windows\\Windows Error Reporting\\LocalDumps\\resodrive.exe"
    };
    for (size_t i = 0; i < ARRAYSIZE(keys); ++i) {
        HKEY key = NULL;
        if (RegOpenKeyExW(HKEY_LOCAL_MACHINE, keys[i], 0, KEY_QUERY_VALUE | KEY_WOW64_64KEY, &key) != ERROR_SUCCESS) continue;
        configuredPolicy = TRUE;
        DWORD size = sizeof(configured), type = 0;
        if (RegQueryValueExW(key, L"DumpFolder", NULL, &type, (BYTE *)configured, &size) == ERROR_SUCCESS &&
            (type == REG_SZ || type == REG_EXPAND_SZ) && size >= sizeof(wchar_t) && size <= sizeof(configured) && configured[size / sizeof(wchar_t) - 1] == L'\0') {
            DWORD expanded = ExpandEnvironmentStringsW(configured, folder, PATH_CAP);
            folderConfigured = expanded != 0 && expanded <= PATH_CAP;
        }
        size = sizeof(DWORD);
        DWORD value = 0;
        if (RegQueryValueExW(key, L"DumpType", NULL, &type, (BYTE *)&value, &size) == ERROR_SUCCESS && type == REG_DWORD && size == sizeof(DWORD)) dumpType = value;
        RegCloseKey(key);
    }
    if (!configuredPolicy) {
        StringCchCopyW(incident.dumpPolicy, ARRAYSIZE(incident.dumpPolicy), L"No readable per-application or global WER LocalDumps policy. A dump is not guaranteed.");
        return;
    }
    if (!folderConfigured) {
        wchar_t *local = NULL;
        if (FAILED(SHGetKnownFolderPath(&FOLDERID_LocalAppData, KF_FLAG_DONT_VERIFY, NULL, &local))) return;
        folderConfigured = join_path(folder, PATH_CAP, local, L"CrashDumps");
        CoTaskMemFree(local);
    }
    StringCchPrintfW(incident.dumpPolicy, ARRAYSIZE(incident.dumpPolicy), L"WER LocalDumps policy is readable; DumpType=%lu. Capture success remains independent of this observer.", dumpType);
    if (!folderConfigured || !incident.pid || !filetime_number(incident.creation)) return;
    if (!local_filesystem_path(folder)) {
        StringCchCatW(incident.dumpPolicy, ARRAYSIZE(incident.dumpPolicy), L" A remote/unsupported dump folder was not probed.");
        return;
    }
    StringCchPrintfW(filename, ARRAYSIZE(filename), L"resodrive.exe.%lu.dmp", incident.pid);
    if (!join_path(candidate, PATH_CAP, folder, filename)) return;
    WIN32_FILE_ATTRIBUTE_DATA attributes;
    if (GetFileAttributesExW(candidate, GetFileExInfoStandard, &attributes) &&
        !(attributes.dwFileAttributes & (FILE_ATTRIBUTE_DIRECTORY | FILE_ATTRIBUTE_REPARSE_POINT)) &&
        filetime_number(attributes.ftCreationTime) >= filetime_number(incident.creation) &&
        filetime_number(attributes.ftLastWriteTime) >= filetime_number(incident.creation) &&
        (attributes.nFileSizeHigh || attributes.nFileSizeLow))
        StringCchCopyW(incident.dump, PATH_CAP, candidate);
}

static void prune_summaries(void) {
    wchar_t pattern[PATH_CAP], oldest[PATH_CAP];
    if (!join_path(pattern, PATH_CAP, incident.diagnostics, L"incident-*.txt")) return;
    // Bounded work even if a directory contains unrelated or malicious entries.
    for (unsigned pass = 0; pass < 128; ++pass) {
        WIN32_FIND_DATAW entry;
        HANDLE search = FindFirstFileW(pattern, &entry);
        if (search == INVALID_HANDLE_VALUE) return;
        unsigned count = 0;
        ULONGLONG oldestTime = MAXULONGLONG;
        oldest[0] = L'\0';
        do {
            if (entry.dwFileAttributes & (FILE_ATTRIBUTE_DIRECTORY | FILE_ATTRIBUTE_REPARSE_POINT)) continue;
            if (++count > 4096) break;
            ULONGLONG time = filetime_number(entry.ftCreationTime);
            if (time < oldestTime && join_path(oldest, PATH_CAP, incident.diagnostics, entry.cFileName)) oldestTime = time;
        } while (FindNextFileW(search, &entry));
        FindClose(search);
        if (count <= MAX_SUMMARIES || !oldest[0] || !DeleteFileW(oldest)) return;
    }
}

// Bounded read-only switches; never render debugger commands or arbitrary registry text.
static void registry_switch(HKEY hive, const wchar_t *path, const wchar_t *name, BOOL stringBoolean, wchar_t result[48]) {
    HKEY key = NULL;
    LSTATUS opened = RegOpenKeyExW(hive, path, 0, KEY_QUERY_VALUE | KEY_WOW64_64KEY, &key);
    StringCchCopyW(result, 48, opened == ERROR_FILE_NOT_FOUND ? L"absent" : L"unavailable");
    if (opened != ERROR_SUCCESS) return;
    BYTE value[32] = {0};
    DWORD bytes = sizeof(value), type = 0;
    LSTATUS read = RegQueryValueExW(key, name, NULL, &type, value, &bytes);
    RegCloseKey(key);
    if (read == ERROR_FILE_NOT_FOUND) StringCchCopyW(result, 48, L"absent");
    else if (read == ERROR_SUCCESS && type == REG_DWORD && bytes == sizeof(DWORD)) {
        DWORD number;
        CopyMemory(&number, value, sizeof(number));
        StringCchPrintfW(result, 48, L"%lu", number);
    } else if (read == ERROR_SUCCESS && stringBoolean && type == REG_SZ && bytes == 2 * sizeof(wchar_t) &&
        (((wchar_t *)value)[0] == L'0' || ((wchar_t *)value)[0] == L'1') && ((wchar_t *)value)[1] == L'\0')
        StringCchPrintfW(result, 48, L"%c", ((wchar_t *)value)[0]);
    else StringCchCopyW(result, 48, L"present but unsupported/unreadable");
}

static void wer_blocker_evidence(wchar_t *result, size_t capacity) {
    const wchar_t *aePath = L"SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\AeDebug";
    const wchar_t *werPath = L"SOFTWARE\\Microsoft\\Windows\\Windows Error Reporting";
    const wchar_t *policyPath = L"SOFTWARE\\Policies\\Microsoft\\Windows\\Windows Error Reporting";
    wchar_t automatic[48], exclusion[48], machine[48], user[48], machinePolicy[48], userPolicy[48];
    const wchar_t *debugger = L"unavailable";
    HKEY key = NULL;
    LSTATUS opened = RegOpenKeyExW(HKEY_LOCAL_MACHINE, aePath, 0, KEY_QUERY_VALUE | KEY_WOW64_64KEY, &key);
    if (opened == ERROR_FILE_NOT_FOUND) debugger = L"absent";
    if (opened == ERROR_SUCCESS) {
        DWORD bytes = 0;
        LSTATUS read = RegQueryValueExW(key, L"Debugger", NULL, NULL, NULL, &bytes);
        debugger = read == ERROR_SUCCESS ? L"present (command excluded)" : read == ERROR_FILE_NOT_FOUND ? L"absent" : L"unavailable";
        RegCloseKey(key);
    }
    registry_switch(HKEY_LOCAL_MACHINE, aePath, L"Auto", TRUE, automatic);
    registry_switch(HKEY_LOCAL_MACHINE, L"SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\AeDebug\\AutoExclusionList", L"resodrive.exe", FALSE, exclusion);
    registry_switch(HKEY_LOCAL_MACHINE, werPath, L"Disabled", FALSE, machine);
    registry_switch(HKEY_CURRENT_USER, werPath, L"Disabled", FALSE, user);
    registry_switch(HKEY_LOCAL_MACHINE, policyPath, L"Disabled", FALSE, machinePolicy);
    registry_switch(HKEY_CURRENT_USER, policyPath, L"Disabled", FALSE, userPolicy);
    StringCchPrintfW(result, capacity, L"Read-only x64 settings: AeDebug Auto=%s; Debugger=%s; resodrive.exe automatic-debugging exclusion=%s; "
        L"WER Disabled machine=%s, user=%s, machine policy=%s, user policy=%s. Automatic debugging can prevent LocalDumps; Disabled values concern WER reporting and do not alone establish LocalDumps availability.",
        automatic, debugger, exclusion, machine, user, machinePolicy, userPolicy);
}

static void record_incident(void) {
    SYSTEMTIME utc;
    wchar_t error[512], reference[128], when[64], os[256], blockers[768];
    BYTE random[8];
    GetSystemTime(&utc);
    ZeroMemory(random, sizeof(random));
    // Failure of entropy is harmless: CREATE_NEW rejects a duplicate report name.
    (void)BCryptGenRandom(NULL, random, sizeof(random), BCRYPT_USE_SYSTEM_PREFERRED_RNG);
    StringCchPrintfW(reference, ARRAYSIZE(reference), L"%04u%02u%02u-%02u%02u%02u-%lu-%02X%02X%02X%02X",
        utc.wYear, utc.wMonth, utc.wDay, utc.wHour, utc.wMinute, utc.wSecond,
        incident.pid, random[0], random[1], random[2], random[3]);
    StringCchPrintfW(when, ARRAYSIZE(when), L"%04u-%02u-%02uT%02u:%02u:%02u.%03uZ",
        utc.wYear, utc.wMonth, utc.wDay, utc.wHour, utc.wMinute, utc.wSecond, utc.wMilliseconds);
    // The supportedOS manifest makes GetVersionEx report the actual Windows build.
    OSVERSIONINFOW version = {0};
    version.dwOSVersionInfoSize = sizeof(version);
#pragma warning(push)
#pragma warning(disable: 4996)
    if (GetVersionExW(&version)) StringCchPrintfW(os, ARRAYSIZE(os), L"Windows %lu.%lu build %lu (x64)", version.dwMajorVersion, version.dwMinorVersion, version.dwBuildNumber);
    else StringCchCopyW(os, ARRAYSIZE(os), L"unavailable");
#pragma warning(pop)
    system_error(incident.exitCode, error, ARRAYSIZE(error));
    dump_evidence();
    wer_blocker_evidence(blockers, ARRAYSIZE(blockers));
    collect_crash_events(incident.pid, incident.creation, incident.eventEvidence, ARRAYSIZE(incident.eventEvidence));
    StringCchPrintfW(incident.details, TEXT_CAP,
        L"ResoDrive incident summary v1\r\nError reference: %s\r\nUTC: %s\r\n"
        L"Event: %s\r\nRole: %s\r\nProcess ID: %lu\r\nProcess creation FILETIME: %llu\r\n"
        L"Failure already displayed by application: %s\r\n"
        L"Exit code: 0x%08lX (%lu)\r\nWindows message: %s\r\n"
        L"Executable: %.4096s\r\nFile version: %s\r\nExecutable file SHA-256 at observer startup: %s\r\nOS: %s\r\n"
        L"Observer inherited error mode: 0x%08lX\r\nObserver normalized error mode: 0x%08lX (child mode is not inspected)\r\n"
        L"WER capture settings: %s\r\n"
        L"Managed startup/exception details: consult the application's guarded logs.\r\n"
        L"Crash dump candidate (contents not validated): %.4096s\r\nDump policy: %s\r\nDump folder preflight: %s\r\n"
        L"Background transfers: status unknown; no process was stopped by this observer.\r\n"
        L"This summary contains process/build metadata only; no credentials, settings, cache, or command-line arguments.\r\n"
        L"Correlated Windows event metadata:\r\n%s",
        reference, when, incident.launchFailed ? L"launch-failed" :
        (incident.exitCode == ACTIVATION_TIMED_OUT ? L"activation-timeout" : L"unexpected-exit"), incident.role,
        incident.pid, filetime_number(incident.creation), incident.exitCode == FAILURE_ALREADY_DISPLAYED ? L"yes" : L"no", incident.exitCode, incident.exitCode, error,
        incident.executable, incident.version, incident.hash, os, incident.inheritedErrorMode, incident.observerErrorMode, blockers,
        incident.dump[0] ? incident.dump : L"No matching nonempty dump was found at reporting time. WER capture is independent; check again later.", incident.dumpPolicy, incident.dumpPreparation, incident.eventEvidence);
    incident.recordingError = ERROR_SUCCESS;
    if (!diagnostics_path() || !safe_directory(incident.diagnostics)) {
        incident.recordingError = GetLastError();
        if (!incident.recordingError) incident.recordingError = ERROR_BAD_PATHNAME;
    } else {
        wchar_t name[160];
        StringCchPrintfW(name, ARRAYSIZE(name), L"incident-%s.txt", reference);
        if (!join_path(incident.report, PATH_CAP, incident.diagnostics, name)) incident.recordingError = ERROR_FILENAME_EXCED_RANGE;
        else {
            HANDLE file = CreateFileW(incident.report, GENERIC_WRITE, FILE_SHARE_READ, NULL, CREATE_NEW,
                FILE_ATTRIBUTE_NORMAL | FILE_FLAG_OPEN_REPARSE_POINT | FILE_FLAG_WRITE_THROUGH, NULL);
            if (file == INVALID_HANDLE_VALUE) incident.recordingError = GetLastError();
            else {
                char utf8[TEXT_CAP * 3];
                int bytes = WideCharToMultiByte(CP_UTF8, 0, incident.details, -1, utf8, sizeof(utf8), NULL, NULL);
                DWORD written = 0;
                if (!bytes || !WriteFile(file, utf8, (DWORD)(bytes - 1), &written, NULL) || written != (DWORD)(bytes - 1))
                    incident.recordingError = GetLastError() ? GetLastError() : ERROR_WRITE_FAULT;
                if (!incident.recordingError && !FlushFileBuffers(file)) incident.recordingError = GetLastError();
                CloseHandle(file);
                if (incident.recordingError) DeleteFileW(incident.report);
            }
        }
    }
    if (incident.recordingError) {
        system_error(incident.recordingError, error, ARRAYSIZE(error));
        StringCchCatW(incident.details, TEXT_CAP, L"\r\nSummary could not be saved: ");
        StringCchCatW(incident.details, TEXT_CAP, error);
        incident.report[0] = L'\0';
    } else prune_summaries();
}

static BOOL copy_details(HWND owner) {
    size_t size = (wcslen(incident.details) + 1) * sizeof(wchar_t);
    HGLOBAL memory = GlobalAlloc(GMEM_MOVEABLE, size);
    if (!memory) return FALSE;
    void *buffer = GlobalLock(memory);
    if (!buffer) { GlobalFree(memory); return FALSE; }
    CopyMemory(buffer, incident.details, size);
    GlobalUnlock(memory);
    if (!OpenClipboard(owner)) { GlobalFree(memory); return FALSE; }
    BOOL copied = EmptyClipboard() && SetClipboardData(CF_UNICODETEXT, memory) != NULL;
    CloseClipboard();
    if (!copied) GlobalFree(memory); // Clipboard owns the allocation only after success.
    return copied;
}

static HRESULT CALLBACK dialog_callback(HWND window, UINT notification, WPARAM button, LPARAM parameter, LONG_PTR context) {
    (void)parameter;
    if (notification == TDN_BUTTON_CLICKED && button == COPY_BUTTON) {
        wchar_t status[2304];
        StringCchPrintfW(status, ARRAYSIZE(status), L"%s\n\n%s", (const wchar_t *)context,
            copy_details(window) ? L"Details copied to the clipboard." :
            L"Windows could not copy the details. Try again, or expand Technical details to read them.");
        SendMessageW(window, TDM_SET_ELEMENT_TEXT, TDE_CONTENT, (LPARAM)status);
        return S_FALSE;
    }
    if (notification == TDN_BUTTON_CLICKED && button == FOLDER_BUTTON) {
        if ((INT_PTR)ShellExecuteW(window, L"open", incident.diagnostics, NULL, NULL, SW_SHOWNORMAL) <= 32) {
            wchar_t status[2304];
            StringCchPrintfW(status, ARRAYSIZE(status), L"%s\n\nWindows could not open the diagnostics folder.", (const wchar_t *)context);
            SendMessageW(window, TDM_SET_ELEMENT_TEXT, TDE_CONTENT, (LPARAM)status);
        }
        return S_FALSE;
    }
    return S_OK;
}

static BOOL acquire_dialog_gate(HANDLE *gate) {
    *gate = NULL;
    HANDLE token = NULL;
    BYTE tokenBuffer[256];
    DWORD required = 0;
    wchar_t *sid = NULL;
    if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &token)) return TRUE;
    BOOL known = GetTokenInformation(token, TokenUser, tokenBuffer, sizeof(tokenBuffer), &required) &&
        ConvertSidToStringSidW(((TOKEN_USER *)tokenBuffer)->User.Sid, &sid);
    CloseHandle(token);
    if (!known) return TRUE;
    // This hash groups dialog ownership only; it is not an authentication decision.
    ULONGLONG directoryHash = 14695981039346656037ULL;
    for (const wchar_t *cursor = incident.directory; *cursor; ++cursor) {
        wchar_t value = *cursor;
        if (value >= L'A' && value <= L'Z') value += L'a' - L'A';
        directoryHash = (directoryHash ^ value) * 1099511628211ULL;
    }
    wchar_t name[256];
    StringCchPrintfW(name, ARRAYSIZE(name), L"Local\\ResoDriveCrashDialog-%s-%016llX-%s", sid, directoryHash, incident.role);
    LocalFree(sid);
    HANDLE mutex = CreateMutexW(NULL, FALSE, name);
    if (!mutex) return TRUE; // IPC failure must not hide the only available error.
    DWORD result = WaitForSingleObject(mutex, 0);
    if (result == WAIT_OBJECT_0 || result == WAIT_ABANDONED) { *gate = mutex; return TRUE; }
    CloseHandle(mutex);
    return result != WAIT_TIMEOUT;
}

static void show_incident(void) {
    if (incident.noDialog || incident.sessionEnding || GetSystemMetrics(SM_SHUTTINGDOWN) || incident.exitCode == FAILURE_ALREADY_DISPLAYED) return;
    HANDLE gate = NULL;
    if (!acquire_dialog_gate(&gate)) return; // Every incident was saved; show one dialog per installation/role.
    INITCOMMONCONTROLSEX controls = {sizeof(controls), ICC_STANDARD_CLASSES};
    (void)InitCommonControlsEx(&controls);
    TASKDIALOG_BUTTON buttons[] = {{COPY_BUTTON, L"Copy details"}, {FOLDER_BUTTON, L"Open diagnostics folder"}, {IDCANCEL, L"Close"}};
    wchar_t content[2048];
    StringCchPrintfW(content, ARRAYSIZE(content),
        L"%s\n\nExit code: 0x%08lX\n%s\n\nBackground transfers may still be running. This report did not stop them.",
        incident.launchFailed ? L"Windows could not start ResoDrive." :
        (incident.exitCode == ACTIVATION_TIMED_OUT ? L"The existing ResoDrive window did not become ready in time. It may still be running." :
        (wcscmp(incident.role, L"host") == 0 ? L"The ResoDrive background process stopped unexpectedly." : L"ResoDrive stopped unexpectedly.")),
        incident.exitCode, incident.recordingError ? L"The diagnostic summary could not be saved. Copy the details below." : L"A diagnostic summary was saved on this PC.");
    TASKDIALOGCONFIG dialog = {0};
    dialog.cbSize = sizeof(dialog);
    dialog.dwFlags = TDF_ALLOW_DIALOG_CANCELLATION | TDF_SIZE_TO_CONTENT;
    dialog.pszWindowTitle = L"ResoDrive";
    dialog.pszMainInstruction = incident.launchFailed ? L"ResoDrive could not start" :
        (incident.exitCode == ACTIVATION_TIMED_OUT ? L"ResoDrive did not respond" : L"ResoDrive closed unexpectedly");
    dialog.pszMainIcon = TD_ERROR_ICON;
    dialog.pszContent = content;
    dialog.pszExpandedInformation = incident.details;
    dialog.pszExpandedControlText = L"Hide technical details";
    dialog.pszCollapsedControlText = L"Technical details";
    dialog.cButtons = incident.recordingError ? 1 : 3;
    dialog.pButtons = buttons;
    if (incident.recordingError) dialog.dwCommonButtons = TDCBF_CLOSE_BUTTON;
    dialog.nDefaultButton = incident.recordingError ? IDCLOSE : IDCANCEL;
    dialog.pfCallback = dialog_callback;
    dialog.lpCallbackData = (LONG_PTR)content;
    if (FAILED(TaskDialogIndirect(&dialog, NULL, NULL, NULL))) {
        // Minimal fallback still works if common controls cannot create the dialog.
        StringCchCatW(content, ARRAYSIZE(content), L"\n\n");
        StringCchCatW(content, ARRAYSIZE(content), incident.details);
        MessageBoxW(NULL, content, L"ResoDrive", MB_OK | MB_ICONERROR | MB_SETFOREGROUND);
    }
    if (gate) { ReleaseMutex(gate); CloseHandle(gate); }
}

static LRESULT CALLBACK shutdown_window(HWND window, UINT message, WPARAM parameter, LPARAM value) {
    if (message == WM_QUERYENDSESSION) { incident.sessionEnding = TRUE; return TRUE; }
    if (message == WM_ENDSESSION) { incident.sessionEnding = parameter != 0; return 0; }
    return DefWindowProcW(window, message, parameter, value);
}

static BOOL wait_for_process(HANDLE process) {
    WNDCLASSW windowClass = {0};
    windowClass.lpfnWndProc = shutdown_window;
    windowClass.hInstance = GetModuleHandleW(NULL);
    windowClass.lpszClassName = L"ResoDriveCrashObserverShutdown";
    (void)RegisterClassW(&windowClass);
    // Hidden top-level window receives shutdown broadcasts, never appears on normal startup.
    HWND window = CreateWindowExW(WS_EX_TOOLWINDOW, windowClass.lpszClassName, L"", 0,
        0, 0, 0, 0, NULL, NULL, windowClass.hInstance, NULL);
    BOOL finished = FALSE;
    for (;;) {
        DWORD result = MsgWaitForMultipleObjects(1, &process, FALSE, INFINITE, QS_ALLINPUT);
        if (result == WAIT_FAILED) break;
        MSG message;
        while (PeekMessageW(&message, NULL, 0, 0, PM_REMOVE)) {
            TranslateMessage(&message);
            DispatchMessageW(&message);
        }
        if (result == WAIT_OBJECT_0) { finished = TRUE; break; }
    }
    if (window) DestroyWindow(window);
    return finished;
}

// The observer accepts only the adjacent physical executable and the exact process instance.
static BOOL verify_observed_process(HANDLE process, ULONGLONG expectedCreation) {
    FILETIME exited, kernel, user;
    wchar_t path[PATH_CAP];
    DWORD length = PATH_CAP;
    if (!GetProcessTimes(process, &incident.creation, &exited, &kernel, &user) ||
        filetime_number(incident.creation) != expectedCreation ||
        !QueryFullProcessImageNameW(process, 0, path, &length)) return FALSE;
    HANDLE actual = CreateFileW(path, FILE_READ_ATTRIBUTES, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
        NULL, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, NULL);
    HANDLE adjacent = CreateFileW(incident.executable, FILE_READ_ATTRIBUTES, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
        NULL, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, NULL);
    BY_HANDLE_FILE_INFORMATION a, b;
    BOOL same = actual != INVALID_HANDLE_VALUE && adjacent != INVALID_HANDLE_VALUE &&
        GetFileInformationByHandle(actual, &a) && GetFileInformationByHandle(adjacent, &b) &&
        a.dwVolumeSerialNumber == b.dwVolumeSerialNumber && a.nFileIndexHigh == b.nFileIndexHigh && a.nFileIndexLow == b.nFileIndexLow;
    if (actual != INVALID_HANDLE_VALUE) CloseHandle(actual);
    if (adjacent != INVALID_HANDLE_VALUE) CloseHandle(adjacent);
    return same;
}

static BOOL parse_decimal(const wchar_t *text, ULONGLONG *result) {
    wchar_t *end;
    if (!text[0]) return FALSE;
    for (const wchar_t *cursor = text; *cursor; ++cursor) if (*cursor < L'0' || *cursor > L'9') return FALSE;
    *result = _wcstoui64(text, &end, 10);
    return *end == L'\0' && *result != 0 && *result != MAXULONGLONG;
}

int WINAPI wWinMain(HINSTANCE instance, HINSTANCE previous, PWSTR commandLine, int show) {
    (void)instance; (void)previous; (void)show;
    // Children inherit this mode. SEM_NOGPFAULTERRORBOX disables WER entirely,
    // so clear it before any worker/child starts, retaining the other flags.
    incident.inheritedErrorMode = GetErrorMode();
    SetErrorMode((incident.inheritedErrorMode & ~SEM_NOGPFAULTERRORBOX) | SEM_FAILCRITICALERRORS);
    incident.observerErrorMode = GetErrorMode();
    incident.role = L"ui";
    wchar_t noDialog[8];
    incident.noDialog = GetEnvironmentVariableW(L"RDRIVE_CRASH_NO_DIALOG", noDialog, ARRAYSIZE(noDialog)) == 1 && noDialog[0] == L'1';
    if (!adjacent_application()) return 2;
    int count = 0;
    wchar_t **arguments = CommandLineToArgvW(GetCommandLineW(), &count);
    if (!arguments) return 2;
    HANDLE process = NULL;
    BOOL utility = FALSE;
    if (count > 1 && _wcsicmp(arguments[1], L"--observe") == 0) {
        ULONGLONG pid = 0, creation = 0;
        if (count < 4 || !parse_decimal(arguments[2], &pid) || pid > MAXDWORD || !parse_decimal(arguments[3], &creation)) { LocalFree(arguments); return 2; }
        incident.pid = (DWORD)pid;
        for (int i = 4; i < count; ++i) {
            if (_wcsicmp(arguments[i], L"--no-dialog") == 0) incident.noDialog = TRUE;
            else if (_wcsicmp(arguments[i], L"--role") == 0 && i + 1 < count) {
                ++i;
                if (_wcsicmp(arguments[i], L"host") == 0) incident.role = L"host";
                else if (_wcsicmp(arguments[i], L"ui") != 0) { LocalFree(arguments); return 2; }
            } else { LocalFree(arguments); return 2; }
        }
        process = OpenProcess(SYNCHRONIZE | PROCESS_QUERY_LIMITED_INFORMATION, FALSE, incident.pid);
        if (!process || !verify_observed_process(process, creation)) {
            if (process) CloseHandle(process);
            LocalFree(arguments); return 2;
        }
        executable_metadata();
        prepare_owned_dump_folder();
    } else {
        for (int i = 1; i < count; ++i) {
            if (_wcsicmp(arguments[i], L"--host") == 0) incident.role = L"host";
        }
        if (count > 1 && (_wcsicmp(arguments[1], L"--prepare-update") == 0 ||
            _wcsicmp(arguments[1], L"--prepare-install") == 0 || _wcsicmp(arguments[1], L"--complete-update") == 0 ||
            _wcsicmp(arguments[1], L"--unregister-autostart") == 0 || _wcsicmp(arguments[1], L"password") == 0 ||
            _wcsicmp(arguments[1], L"password-file") == 0)) utility = TRUE;
        executable_metadata();
        prepare_owned_dump_folder();
        wchar_t *childCommand = HeapAlloc(GetProcessHeap(), HEAP_ZERO_MEMORY, PATH_CAP * sizeof(wchar_t));
        if (!childCommand || FAILED(StringCchPrintfW(childCommand, PATH_CAP, L"\"%s\" %s", incident.executable, commandLine))) {
            if (childCommand) HeapFree(GetProcessHeap(), 0, childCommand);
            LocalFree(arguments); return 2;
        }
        // Launcher has no worker threads. Setting this process-local flag cannot affect other apps.
        SetEnvironmentVariableW(L"RDRIVE_CRASH_SUPERVISED", L"1");
        STARTUPINFOW startup = {0};
        PROCESS_INFORMATION launched = {0};
        startup.cb = sizeof(startup);
        if (!CreateProcessW(incident.executable, childCommand, NULL, NULL, FALSE, 0, NULL, incident.directory, &startup, &launched)) {
            incident.launchFailed = TRUE;
            incident.exitCode = GetLastError();
        } else {
            process = launched.hProcess;
            incident.pid = launched.dwProcessId;
            FILETIME exited, kernel, user;
            (void)GetProcessTimes(process, &incident.creation, &exited, &kernel, &user);
            CloseHandle(launched.hThread);
        }
        HeapFree(GetProcessHeap(), 0, childCommand);
    }
    LocalFree(arguments);
    if (process) {
        if (!wait_for_process(process) || !GetExitCodeProcess(process, &incident.exitCode)) { CloseHandle(process); return 2; }
    }
    BOOL report = (incident.launchFailed || incident.exitCode != 0) && !utility && !incident.sessionEnding && !GetSystemMetrics(SM_SHUTTINGDOWN);
    // Keep the kernel process object referenced through collection: its PID cannot
    // be reused while this handle is open, including for delayed event records.
    if (report) record_incident();
    if (process) CloseHandle(process);
    if (report) show_incident();
    return (int)incident.exitCode;
}
