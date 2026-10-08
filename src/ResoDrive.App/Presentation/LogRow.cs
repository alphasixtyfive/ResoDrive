using System.Globalization;
using MediaBrush = System.Windows.Media.Brush;

namespace ResoDrive.App;

public enum LogSeverity { Information, Success, Warning, Error }

public sealed record LogRow(
    string Title,
    string Detail,
    DateTimeOffset OccurredAt,
    LogSeverity Severity
)
{
    public string Time => OccurredAt.LocalDateTime.ToString("dd MMM HH:mm:ss", CultureInfo.CurrentCulture);
    public string FullTime => OccurredAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture);
    public string AccessibleName => $"{FullTime} · {Severity} · {Title} · {Detail}";
    public MediaBrush Brush => Severity switch
    {
        LogSeverity.Error => StatusPalette.Error,
        LogSeverity.Warning => StatusPalette.Warning,
        LogSeverity.Success => StatusPalette.Success,
        _ => StatusPalette.Muted
    };
    public string SeverityGlyph => Severity switch
    {
        LogSeverity.Error => "\uE783",
        LogSeverity.Warning => "\uE7BA",
        LogSeverity.Success => "\uE73E",
        _ => "\uE946"
    };
}
