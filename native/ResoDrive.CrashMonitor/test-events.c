// Pure metadata/correlation checks: no event writes, crash or machine policy changes.
#define WIN32_LEAN_AND_MEAN
#include "event-evidence.h"
#include <wchar.h>

static EVT_VARIANT values[EventValueCount];
static wchar_t text[4096];
static void string_value(unsigned index, const wchar_t *value) {
    values[index].Type = EvtVarTypeString;
    values[index].StringVal = value;
}
static void number_value(unsigned index, ULONGLONG value) {
    values[index].Type = EvtVarTypeUInt64;
    values[index].UInt64Val = value;
}
static BOOL render(void) {
    text[0] = L'\0';
    return append_correlated_event(values, EventValueCount, 123, 1000, 2000, text, ARRAYSIZE(text));
}
int main(void) {
    string_value(EventProvider, L"Application Error"); number_value(EventId, 1000);
    number_value(EventTime, 1500); number_value(EventRecord, 987);
    string_value(EventCrashPid, L"0x7b"); string_value(EventCreationTime, L"0x3e8");
    string_value(EventAppName, L"resodrive.exe"); string_value(EventModule, L"KERNELBASE.dll");
    string_value(EventModuleVersion, L"10.0.19041.1503");
    values[EventExceptionCode].Type = EvtVarTypeHexInt32; values[EventExceptionCode].UInt32Val = 0x80131506;
    string_value(EventFaultOffset, L"000000000010b382");
    string_value(EventReportId, L"734b7842-1bb0-46c1-a784-216cedb70ce3");
    if (!render() || !wcsstr(text, L"KERNELBASE.dll") || !wcsstr(text, L"0x80131506") || !wcsstr(text, L"record 987")) return 1;
    string_value(EventCrashPid, L"0x7c"); if (render() || text[0]) return 2;
    string_value(EventCrashPid, L"0x7b"); string_value(EventCreationTime, L"0x3e7"); if (render()) return 3;
    string_value(EventCreationTime, L"0x3e8"); number_value(EventTime, 999); if (render()) return 4;
    number_value(EventTime, 2001); if (render()) return 5;
    number_value(EventTime, 1500); string_value(EventAppName, L"other-app.exe"); if (render()) return 6;
    string_value(EventAppName, L"resodrive.exe"); string_value(EventProvider, L"other-provider"); if (render()) return 7;
    string_value(EventProvider, L".NET Runtime"); number_value(EventId, 1023); number_value(EventExecutionPid, 0); if (render()) return 8;
    number_value(EventExecutionPid, 123); string_value(EventRuntimeVersion, L"10.0.11"); if (!render() || !wcsstr(text, L"Runtime version: 10.0.11")) return 9;
    string_value(EventProvider, L"Windows Error Reporting"); number_value(EventId, 1001); number_value(EventExecutionPid, 456); if (render()) return 10;
    string_value(EventProvider, L"Application Error"); number_value(EventId, 1000); string_value(EventModule, L"module\r\nInjected: data");
    if (!render() || wcsstr(text, L"\r\nInjected:")) return 11;
    if (append_correlated_event(values, EventValueCount - 1, 123, 1000, 2000, text, ARRAYSIZE(text))) return 12;
    string_value(EventModule, L"C:\\private\\password-canary.dll"); string_value(EventModuleVersion, L"version-token-canary");
    string_value(EventReportId, L"Bearer secret-canary"); string_value(EventRuntimeVersion, L"runtime-token-canary");
    string_value(EventExceptionCode, L"exception-token-canary"); string_value(EventFaultOffset, L"offset-token-canary");
    if (!render() || wcsstr(text, L"canary") || wcsstr(text, L"C:\\private")) return 13;
    return 0;
}
