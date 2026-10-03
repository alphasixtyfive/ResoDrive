using ResoDrive.Core.Results;
using ResoDrive.Core.Settings;
using ResoDrive.Windows;

namespace ResoDrive.App;

internal static class DriveOrder
{
    public static MountSettings[] Move(IReadOnlyList<MountSettings> mounts,
        Guid sourceId, Guid targetId, bool after)
    {
        ArgumentNullException.ThrowIfNull(mounts);
        if (mounts.Select(mount => mount.Id).Distinct().Count() != mounts.Count)
            throw new ArgumentException("Drive order requires unique drive IDs.", nameof(mounts));
        var ordered = mounts.ToList();
        var source = ordered.FindIndex(mount => mount.Id == sourceId);
        var target = ordered.FindIndex(mount => mount.Id == targetId);
        if (source < 0 || target < 0 || source == target)
            return mounts.ToArray();
        var moving = ordered[source];
        ordered.RemoveAt(source);
        target = ordered.FindIndex(mount => mount.Id == targetId);
        ordered.Insert(target + (after ? 1 : 0), moving);
        return ordered.ToArray();
    }

    public static async Task<OperationResult<ManagerSettings>> SaveAsync(
        AtomicSettingsStore store, ManagerSettings current, Guid sourceId, Guid targetId, bool after,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(current);
        var ordered = Move(current.Mounts, sourceId, targetId, after);
        if (ordered.Select(mount => mount.Id).SequenceEqual(current.Mounts.Select(mount => mount.Id)))
            return Result.Success(current);
        // Publish the new view order only after this revision-checked atomic write succeeds.
        return await store.SaveAsync(current with { Mounts = ordered }, current.Revision, cancellationToken)
            .ConfigureAwait(false);
    }
}
