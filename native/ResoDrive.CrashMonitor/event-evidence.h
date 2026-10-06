#pragma once
#include <windows.h>
#include <winevt.h>

// Fixed render paths: never persist raw event XML, event messages or arbitrary data.
enum CrashEventValue {
    EventProvider, EventId, EventTime, EventRecord, EventExecutionPid,
    EventCrashPid, EventCreationTime, EventAppName, EventModule, EventModuleVersion,
    EventExceptionCode, EventFaultOffset, EventReportId, EventRuntimeVersion, EventValueCount
};
BOOL append_correlated_event(const EVT_VARIANT *values, DWORD count, DWORD pid,
    ULONGLONG creation, ULONGLONG now, wchar_t *text, size_t capacity);
void collect_crash_events(DWORD pid, FILETIME creation, wchar_t *text, size_t capacity);
