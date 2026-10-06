#define WIN32_LEAN_AND_MEAN
#define _CRT_SECURE_NO_WARNINGS
#include "event-evidence.h"
#include <strsafe.h>
#include <stdlib.h>
#include <wchar.h>
#include <wctype.h>

static BOOL event_number(const EVT_VARIANT *value, ULONGLONG *number) {
    switch (value->Type) {
    case EvtVarTypeUInt16: *number = value->UInt16Val; return TRUE;
    case EvtVarTypeUInt32: case EvtVarTypeHexInt32: *number = value->UInt32Val; return TRUE;
    case EvtVarTypeUInt64: case EvtVarTypeHexInt64: case EvtVarTypeFileTime: *number = value->UInt64Val; return TRUE;
    case EvtVarTypeString: {
        const wchar_t *text = value->StringVal;
        wchar_t *end;
        if (!text || !text[0] || text[0] == L'-' || text[0] == L'+') return FALSE;
        *number = _wcstoui64(text, &end, 0);
        return *end == L'\0' && *number != MAXULONGLONG;
    }
    default: return FALSE;
    }
}

static const wchar_t *event_string(const EVT_VARIANT *value) {
    return value->Type == EvtVarTypeString && value->StringVal ? value->StringVal : L"";
}

enum FieldFormat { ModuleLeaf, NumericVersion, HexNumber, ReportGuid };

static BOOL valid_field(const wchar_t *text, enum FieldFormat format) {
    size_t length = wcslen(text);
    if (!length || length > 256) return FALSE;
    if (format == ModuleLeaf) {
        if (_wcsicmp(text, L"unknown") == 0) return TRUE;
        const wchar_t *extension = wcsrchr(text, L'.');
        if (!extension || (_wcsicmp(extension, L".dll") != 0 && _wcsicmp(extension, L".exe") != 0)) return FALSE;
        for (size_t i = 0; i < length; ++i)
            if (!(iswalnum(text[i]) || text[i] == L'.' || text[i] == L'-' || text[i] == L'_' || text[i] == L' ')) return FALSE;
        return TRUE;
    }
    if (format == NumericVersion) {
        if (length > 64 || text[0] < L'0' || text[0] > L'9' || text[length - 1] < L'0' || text[length - 1] > L'9') return FALSE;
        for (size_t i = 0; i < length; ++i) if (!(text[i] >= L'0' && text[i] <= L'9') && text[i] != L'.') return FALSE;
        return TRUE;
    }
    if (format == HexNumber) {
        size_t start = length > 2 && text[0] == L'0' && (text[1] == L'x' || text[1] == L'X') ? 2 : 0;
        if (length - start > 16 || length == start) return FALSE;
        for (size_t i = start; i < length; ++i) if (!iswxdigit(text[i])) return FALSE;
        return TRUE;
    }
    if (format == ReportGuid) {
        if (length == 38 && text[0] == L'{' && text[37] == L'}') { ++text; length -= 2; }
        if (length != 36) return FALSE;
        for (size_t i = 0; i < length; ++i) {
            if (i == 8 || i == 13 || i == 18 || i == 23) { if (text[i] != L'-') return FALSE; }
            else if (!iswxdigit(text[i])) return FALSE;
        }
        return TRUE;
    }
    return FALSE;
}

static void append_field(wchar_t *text, size_t capacity, const wchar_t *label, const EVT_VARIANT *value, enum FieldFormat format) {
    wchar_t clean[257];
    wchar_t numeric[32];
    const wchar_t *source = event_string(value);
    ULONGLONG number;
    if (value->Type != EvtVarTypeString && event_number(value, &number)) {
        StringCchPrintfW(numeric, ARRAYSIZE(numeric), L"0x%llX", number);
        source = numeric;
    }
    if (!valid_field(source, format) || FAILED(StringCchCopyW(clean, ARRAYSIZE(clean), source))) return;
    StringCchCatW(text, capacity, L"  ");
    StringCchCatW(text, capacity, label);
    StringCchCatW(text, capacity, L": ");
    StringCchCatW(text, capacity, clean);
    StringCchCatW(text, capacity, L"\r\n");
}

BOOL append_correlated_event(const EVT_VARIANT *values, DWORD count, DWORD pid,
    ULONGLONG creation, ULONGLONG now, wchar_t *text, size_t capacity) {
    ULONGLONG id, time, actualPid = 0, actualCreation = 0, record = 0;
    if (count != EventValueCount || !pid || !creation ||
        !event_number(&values[EventId], &id) || !event_number(&values[EventTime], &time) ||
        time < creation || time > now) return FALSE;
    const wchar_t *provider = event_string(&values[EventProvider]);
    if (id == 1000 && _wcsicmp(provider, L"Application Error") == 0) {
        if (!event_number(&values[EventCrashPid], &actualPid) || actualPid != pid ||
            !event_number(&values[EventCreationTime], &actualCreation) || actualCreation != creation ||
            _wcsicmp(event_string(&values[EventAppName]), L"resodrive.exe") != 0) return FALSE;
    } else if (((id == 1023 || id == 1026) && _wcsicmp(provider, L".NET Runtime") == 0) ||
        (id == 1001 && _wcsicmp(provider, L"Windows Error Reporting") == 0)) {
        // These providers sometimes report PID 0 or WerFault's PID; never guess a match.
        if (!event_number(&values[EventExecutionPid], &actualPid) || actualPid != pid) return FALSE;
    } else return FALSE;
    (void)event_number(&values[EventRecord], &record);
    wchar_t heading[256];
    StringCchPrintfW(heading, ARRAYSIZE(heading), L"Event: %s / %llu; record %llu; UTC FILETIME %llu\r\n", provider, id, record, time);
    StringCchCatW(text, capacity, heading);
    append_field(text, capacity, L"Fault module", &values[EventModule], ModuleLeaf);
    append_field(text, capacity, L"Module version", &values[EventModuleVersion], NumericVersion);
    append_field(text, capacity, L"Exception code", &values[EventExceptionCode], HexNumber);
    append_field(text, capacity, L"Fault offset", &values[EventFaultOffset], HexNumber);
    append_field(text, capacity, L"Report ID", &values[EventReportId], ReportGuid);
    append_field(text, capacity, L"Runtime version", &values[EventRuntimeVersion], NumericVersion);
    return TRUE;
}

void collect_crash_events(DWORD pid, FILETIME creation, wchar_t *text, size_t capacity) {
    text[0] = L'\0';
    DWORD collectionError = ERROR_SUCCESS;
    ULARGE_INTEGER started, now;
    started.LowPart = creation.dwLowDateTime; started.HighPart = creation.dwHighDateTime;
    FILETIME current;
    GetSystemTimeAsFileTime(&current);
    now.LowPart = current.dwLowDateTime; now.HighPart = current.dwHighDateTime;
    if (!pid || !started.QuadPart) goto unavailable;
    SYSTEMTIME utc;
    if (!FileTimeToSystemTime(&creation, &utc)) goto unavailable;
    wchar_t queryText[512];
    StringCchPrintfW(queryText, ARRAYSIZE(queryText),
        L"*[System[(EventID=1000 or EventID=1001 or EventID=1023 or EventID=1026) and TimeCreated[@SystemTime>='%04u-%02u-%02uT%02u:%02u:%02u.%03uZ']]]",
        utc.wYear, utc.wMonth, utc.wDay, utc.wHour, utc.wMinute, utc.wSecond, utc.wMilliseconds);
    EVT_HANDLE query = EvtQuery(NULL, L"Application", queryText, EvtQueryChannelPath | EvtQueryReverseDirection);
    if (!query) { collectionError = GetLastError(); goto unavailable; }
    const wchar_t *paths[EventValueCount] = {
        L"Event/System/Provider/@Name", L"Event/System/EventID", L"Event/System/TimeCreated/@SystemTime",
        L"Event/System/EventRecordID", L"Event/System/Execution/@ProcessID",
        L"Event/EventData/Data[@Name='ProcessId']", L"Event/EventData/Data[@Name='ProcessCreationTime']",
        L"Event/EventData/Data[@Name='AppName']", L"Event/EventData/Data[@Name='ModuleName']",
        L"Event/EventData/Data[@Name='ModuleVersion']", L"Event/EventData/Data[@Name='ExceptionCode']",
        L"Event/EventData/Data[@Name='FaultingOffset']", L"Event/EventData/Data[@Name='IntegratorReportId']",
        L"Event/EventData/Data[@Name='RuntimeVersion']"
    };
    EVT_HANDLE context = EvtCreateRenderContext(EventValueCount, paths, EvtRenderContextValues);
    if (!context) collectionError = GetLastError();
    unsigned matches = 0;
    ULONGLONG deadline = GetTickCount64() + 1000;
    if (context) {
        for (unsigned inspected = 0; inspected < 128 && matches < 4; ++inspected) {
            if (GetTickCount64() >= deadline) break;
            EVT_HANDLE event;
            DWORD returned = 0;
            if (!EvtNext(query, 1, &event, 100, 0, &returned)) {
                DWORD error = GetLastError();
                if (error != ERROR_NO_MORE_ITEMS) collectionError = error;
                break;
            }
            DWORD bytes = 0, count = 0;
            if (!EvtRender(context, event, EvtRenderEventValues, 0, NULL, &bytes, &count) &&
                GetLastError() == ERROR_INSUFFICIENT_BUFFER && bytes <= 64 * 1024) {
                EVT_VARIANT *values = HeapAlloc(GetProcessHeap(), 0, bytes);
                if (values && EvtRender(context, event, EvtRenderEventValues, bytes, values, &bytes, &count) &&
                    append_correlated_event(values, count, pid, started.QuadPart, now.QuadPart, text, capacity)) ++matches;
                if (values) HeapFree(GetProcessHeap(), 0, values);
            }
            EvtClose(event);
        }
        EvtClose(context);
    }
    EvtClose(query);
    if (matches) return;
unavailable:
    StringCchPrintfW(text, capacity, L"No exact process-correlated event metadata was available at reporting time (Event API error %lu). Event logging may be delayed or unavailable; unrelated events were excluded.\r\n", collectionError);
}
