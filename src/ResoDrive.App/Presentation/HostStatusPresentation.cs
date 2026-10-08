using ResoDrive.Windows;

namespace ResoDrive.App;

internal static class HostStatusPresentation
{
    public static bool HasUsableMountStatus(HostResponse response) =>
        response.Succeeded && response.InitializationErrorCode is null;

    public static bool HasUsableSyncStatus(HostResponse response) =>
        HasUsableMountStatus(response) && response.SyncJobs is not null;
}

