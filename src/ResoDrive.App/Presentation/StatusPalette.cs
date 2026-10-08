using System.Windows.Media;
using MediaBrush = System.Windows.Media.Brush;

namespace ResoDrive.App;

internal static class StatusPalette
{
    private static readonly MediaBrush SuccessBrush = Create("#6CCB5F");
    private static readonly MediaBrush ErrorBrush = Create("#FF7878");
    private static readonly MediaBrush WarningBrush = Create("#FFB946");
    private static readonly MediaBrush InfoBrush = Create("#60CDFF");
    private static readonly MediaBrush MutedBrush = Create("#A9A9A9");
    private static readonly MediaBrush DisabledBrush = Create("#919191");

    public static MediaBrush Success => Select(SuccessBrush, System.Windows.SystemColors.WindowTextBrush);
    public static MediaBrush Error => Select(ErrorBrush, System.Windows.SystemColors.WindowTextBrush);
    public static MediaBrush Warning => Select(WarningBrush, System.Windows.SystemColors.WindowTextBrush);
    public static MediaBrush Info => Select(InfoBrush, System.Windows.SystemColors.WindowTextBrush);
    public static MediaBrush Muted => Select(MutedBrush, System.Windows.SystemColors.GrayTextBrush);
    public static MediaBrush Disabled => Select(DisabledBrush, System.Windows.SystemColors.GrayTextBrush);

    private static MediaBrush Select(MediaBrush standard, MediaBrush highContrast) =>
        System.Windows.SystemParameters.HighContrast ? highContrast : standard;

    private static SolidColorBrush Create(string value)
    {
        var brush = new SolidColorBrush(
            (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(value));
        brush.Freeze();
        return brush;
    }
}

