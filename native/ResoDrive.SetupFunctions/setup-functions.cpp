#include <windows.h>
#include <commctrl.h>
#include <msi.h>
#include <strsafe.h>
#include <shellapi.h>
#include <new>
#include <BootstrapperApplicationTypes.h>
#include <BAFunctions.h>
#include "selection.h"

// WixStdBA owns the UI, elevation and install lifecycle. This extension only
// reads Windows/MSI capabilities and validates the selection before planning.
struct SetupContext
{
    IBootstrapperEngine* engine;
    LONGLONG installedMode;
    BOOL canChangeMode;
    BOOL verified;
};
static SetupContext* context;

static HRESULT SetFailure(SetupContext* state, LPCWSTR message)
{
    state->verified = FALSE;
    state->engine->Log(BOOTSTRAPPER_LOG_LEVEL_ERROR, message);
    HRESULT hr = state->engine->SetVariableString(L"ResoDriveSetupError", message, FALSE);
    if (FAILED(hr)) return hr;
    return state->engine->SetVariableNumeric(L"ResoDriveSelectionValid", 0);
}

static HRESULT ReadString(IBootstrapperEngine* engine, LPCWSTR name, LPWSTR value, SIZE_T capacity)
{
    return engine->GetVariableString(name, value, &capacity);
}

static HRESULT DetectInstalledMode(SetupContext* state)
{
    WCHAR upgradeCode[40], productCode[40], otherProduct[40], version[64];
    HRESULT hr = ReadString(state->engine, L"ResoDriveUpgradeCode", upgradeCode, ARRAYSIZE(upgradeCode));
    if (FAILED(hr)) return hr;
    UINT result = MsiEnumRelatedProductsW(upgradeCode, 0, 0, productCode);
    if (result == ERROR_NO_MORE_ITEMS) return S_OK;
    if (result != ERROR_SUCCESS)
        return SetFailure(state, L"The installed ResoDrive product could not be verified. Repair it in Windows Settings before continuing.");
    result = MsiEnumRelatedProductsW(upgradeCode, 0, 1, otherProduct);
    if (result != ERROR_NO_MORE_ITEMS)
        return SetFailure(state, L"Multiple or unreadable ResoDrive installations are registered. Repair or remove them in Windows Settings before continuing.");
    DWORD length = ARRAYSIZE(version);
    result = MsiGetProductInfoW(productCode, INSTALLPROPERTY_VERSIONSTRING, version, &length);
    if (result != ERROR_SUCCESS)
        return SetFailure(state, L"The installed ResoDrive version could not be verified. Repair it in Windows Settings before continuing.");
    WCHAR assignment[4];
    length = ARRAYSIZE(assignment);
    if (MsiGetProductInfoW(productCode, INSTALLPROPERTY_ASSIGNMENTTYPE, assignment, &length) != ERROR_SUCCESS || wcscmp(assignment, L"1"))
        return SetFailure(state, L"The installed ResoDrive machine registration could not be verified. Repair or remove it in Windows Settings before continuing.");

    // Old packages have no mode component. A registered mode component requires
    // its receipt: a missing/corrupt receipt must never silently change variants.
    WCHAR componentPath[MAX_PATH];
    length = ARRAYSIZE(componentPath);
    INSTALLSTATE component = MsiGetComponentPathW(productCode,
        L"{EB2D8890-7E48-4200-9921-8BE34D12E2A7}", componentPath, &length);
    if (component == INSTALLSTATE_LOCAL || component == INSTALLSTATE_MOREDATA)
    {
        WCHAR mode[32] = {};
        DWORD bytes = sizeof(mode);
        LSTATUS registry = RegGetValueW(HKEY_LOCAL_MACHINE, L"SOFTWARE\\ResoDrive\\Installation",
            L"CompatibilityMode", RRF_RT_REG_SZ | RRF_SUBKEY_WOW6464KEY, NULL, mode, &bytes);
        if (registry != ERROR_SUCCESS || (wcscmp(mode, L"standard") && wcscmp(mode, L"cet-disabled")))
            return SetFailure(state, L"The installed ResoDrive compatibility choice could not be verified. Repair it in Windows Settings before continuing.");
        state->installedMode = wcscmp(mode, L"cet-disabled") == 0 ? 1 : 0;
    }
    else if (component != INSTALLSTATE_UNKNOWN)
        return SetFailure(state, L"The installed ResoDrive compatibility component could not be verified. Repair it in Windows Settings before continuing.");
    else
    {
        // 0.3.31 is the first immutable package with the mode component. Missing
        // metadata in those packages is damage, rather than a legacy default.
        int modeVersion = 0;
        hr = state->engine->CompareVersions(version, L"0.3.31", &modeVersion);
        if (FAILED(hr)) return hr;
        if (modeVersion >= 0)
            return SetFailure(state, L"The installed ResoDrive compatibility receipt is missing. Repair it in Windows Settings before continuing.");
    }

    WCHAR targetVersion[64];
    hr = ReadString(state->engine, L"WixBundleVersion", targetVersion, ARRAYSIZE(targetVersion));
    if (FAILED(hr)) return hr;
    int comparison = 0;
    hr = state->engine->CompareVersions(version, targetVersion, &comparison);
    if (FAILED(hr)) return hr;
    state->canChangeMode = comparison < 0;
    return state->engine->SetVariableString(L"ResoDriveInstalledVersion", version, FALSE);
}

static HRESULT DetectCapabilities(SetupContext* state)
{
    HMODULE kernel = GetModuleHandleW(L"kernel32.dll");
    BOOL hasApc = kernel && GetProcAddress(kernel, "QueueUserAPC2") != NULL;
    PROCESS_MITIGATION_USER_SHADOW_STACK_POLICY policy = {};
    BOOL policyKnown = GetProcessMitigationPolicy(GetCurrentProcess(), ProcessUserShadowStackPolicy, &policy, sizeof(policy));
    WCHAR detail[384];
    HRESULT hr = StringCchPrintfW(detail, ARRAYSIZE(detail),
        L"ResoDrive capability check: QueueUserAPC2 export=%u, Setup shadow-stack policy known=%u, flags=0x%08X. Export presence does not verify all .NET APC capabilities or the future app's policy.",
        hasApc ? 1u : 0u, policyKnown ? 1u : 0u, policy.Flags);
    if (FAILED(hr)) return hr;
    state->engine->Log(BOOTSTRAPPER_LOG_LEVEL_STANDARD, detail);
    return state->engine->SetVariableNumeric(L"ResoDriveMissingCetCapability", hasApc ? 0 : 1);
}

static HRESULT ValidateSelection(SetupContext* state)
{
    LONGLONG selection;
    HRESULT hr = state->engine->GetVariableNumeric(L"ResoDriveCompatibilityMode", &selection);
    if (hr == DISP_E_TYPEMISMATCH) return SetFailure(state, SelectionError(-1, 0, TRUE));
    if (FAILED(hr)) return hr;
    LPCWSTR error = SelectionError(selection, state->installedMode, state->canChangeMode);
    if (error) return SetFailure(state, error);
    return S_OK;
}

static HRESULT WINAPI FunctionsProc(BA_FUNCTIONS_MESSAGE message, const LPVOID, LPVOID results, LPVOID instance)
{
    auto state = static_cast<SetupContext*>(instance);
    if (message == BA_FUNCTIONS_MESSAGE_ONPLANBEGIN)
    {
        HRESULT hr = state->verified ? ValidateSelection(state) : E_INVALIDARG;
        if (FAILED(hr) || !state->verified)
            static_cast<BA_ONPLANBEGIN_RESULTS*>(results)->fCancel = TRUE;
        return S_OK;
    }
    // Theme events require success even when this extension does not handle
    // them. Leave their results unchanged so WixStdBA's normal behavior runs.
    return S_OK;
}

extern "C" __declspec(dllexport) HRESULT WINAPI BAFunctionsCreate(
    const BA_FUNCTIONS_CREATE_ARGS* args, BA_FUNCTIONS_CREATE_RESULTS* results)
{
    if (!args || !results || !args->pEngine || !args->pCommand || context ||
        args->cbSize < sizeof(*args) || results->cbSize < sizeof(*results)) return E_INVALIDARG;
    context = new (std::nothrow) SetupContext{ args->pEngine, 0, TRUE, TRUE };
    if (!context) return E_OUTOFMEMORY;
    context->engine->AddRef();
    HRESULT hr = DetectInstalledMode(context);
    if (SUCCEEDED(hr)) hr = DetectCapabilities(context);
    LONGLONG choice = -1;
    if (SUCCEEDED(hr)) hr = context->engine->GetVariableNumeric(L"ResoDriveCompatibilityMode", &choice);
    if (hr == DISP_E_TYPEMISMATCH) hr = SetFailure(context, SelectionError(-1, 0, TRUE));
    // -1 is the authored sentinel. Explicit 0/1 overrides stay explicit.
    if (SUCCEEDED(hr) && context->verified && choice == -1)
    {
        // WixStdBA parses overridable variables before loading BAFunctions.
        // Distinguish its default sentinel from an explicit invalid -1 override.
        int count = 0;
        BOOL explicitMode = FALSE;
        if (args->pCommand->wzCommandLine && *args->pCommand->wzCommandLine)
        {
            LPWSTR* arguments = CommandLineToArgvW(args->pCommand->wzCommandLine, &count);
            if (!arguments) hr = HRESULT_FROM_WIN32(GetLastError());
            else
            {
                constexpr WCHAR prefix[] = L"ResoDriveCompatibilityMode=";
                for (int index = 0; index < count; ++index)
                    if (wcsncmp(arguments[index], prefix, ARRAYSIZE(prefix) - 1) == 0) explicitMode = TRUE;
                LocalFree(arguments);
            }
        }
        if (SUCCEEDED(hr))
            hr = explicitMode ? SetFailure(context, SelectionError(-1, 0, TRUE)) :
                context->engine->SetVariableNumeric(L"ResoDriveCompatibilityMode", context->installedMode);
    }
    if (SUCCEEDED(hr)) hr = context->engine->SetVariableNumeric(L"ResoDriveCanChangeMode", context->canChangeMode ? 1 : 0);
    if (SUCCEEDED(hr) && context->verified) hr = ValidateSelection(context);
    LONGLONG suppressLaunch = 0;
    if (SUCCEEDED(hr)) hr = context->engine->GetVariableNumeric(L"ResoDriveSuppressLaunch", &suppressLaunch);
    if (hr == DISP_E_TYPEMISMATCH) hr = SetFailure(context, SuppressLaunchError(-1));
    if (SUCCEEDED(hr))
    {
        LPCWSTR error = SuppressLaunchError(suppressLaunch);
        if (error) hr = SetFailure(context, error);
        else if (suppressLaunch == 1)
        {
            // NULL removes this standard Burn variable. An empty string would
            // leave it present and WixStdBA would still enable the Launch button.
            hr = context->engine->SetVariableString(L"LaunchTarget", NULL, FALSE);
        }
    }
    if (FAILED(hr))
    {
        context->engine->Release();
        delete context;
        context = NULL;
        return hr;
    }
    results->pfnBAFunctionsProc = FunctionsProc;
    results->pvBAFunctionsProcContext = context;
    return S_OK;
}

extern "C" __declspec(dllexport) void WINAPI BAFunctionsDestroy(
    const BA_FUNCTIONS_DESTROY_ARGS*, BA_FUNCTIONS_DESTROY_RESULTS*)
{
    if (context)
    {
        context->engine->Release();
        delete context;
        context = NULL;
    }
}
