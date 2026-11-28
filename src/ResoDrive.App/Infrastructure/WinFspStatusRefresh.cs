using ResoDrive.Core.Contracts;
using ResoDrive.Core.Results;
using ResoDrive.Windows;

namespace ResoDrive.App;

/// <summary>Coalesces prerequisite checks requested by one window's UI dispatcher.</summary>
internal sealed class WinFspStatusRefresh(
    Func<CancellationToken, Task<OperationResult<PrerequisiteStatus>>>? inspect = null,
    TimeProvider? clock = null)
{
    private readonly Func<CancellationToken, Task<OperationResult<PrerequisiteStatus>>> _inspect =
        inspect ?? WinFspPrerequisiteService.InspectAsync;
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private Task<OperationResult<PrerequisiteStatus>>? _pending;
    private long? _checkedAt;

    internal async Task<OperationResult<PrerequisiteStatus>?> CheckAsync(bool force, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (_pending is { } pending) return await pending;
        if (!force && _checkedAt is long timestamp &&
            _clock.GetElapsedTime(timestamp) < TimeSpan.FromSeconds(5)) return null;

        try
        {
            var check = _inspect(token);
            _pending = check;
            return await check;
        }
        finally
        {
            _pending = null;
            _checkedAt = _clock.GetTimestamp();
        }
    }
}
