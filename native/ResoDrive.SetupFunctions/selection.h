#pragma once

inline LPCWSTR SelectionError(LONGLONG selection, LONGLONG installedMode, BOOL canChangeMode)
{
    if (selection != 0 && selection != 1)
        return L"ResoDriveCompatibilityMode must be 0 (standard) or 1 (CET compatibility).";
    if (!canChangeMode && selection != installedMode)
        return L"This ResoDrive version is already installed with a different compatibility choice. Repair preserves that choice; use a newer release to change it.";
    return NULL;
}

inline LPCWSTR SuppressLaunchError(LONGLONG value)
{
    return value == 0 || value == 1 ? NULL :
        L"ResoDriveSuppressLaunch must be 0 (offer to open ResoDrive) or 1 (defer opening to the coordinating installer).";
}
