using System.Globalization;
using System.Text.RegularExpressions;

namespace ResoDrive.Core.Validation;

internal static partial class RcloneOptionValue
{
    public static string? Error(string option, string value)
    {
        switch (option.ToLowerInvariant())
        {
            case "--vfs-cache-mode":
                return value.ToLowerInvariant() is "full" or "writes" or "minimal" or "off"
                    ? null : "Choose full, writes, minimal, or off.";
            case "--vfs-cache-max-size":
            case "--vfs-cache-min-free-space":
            case "--vfs-read-chunk-size-limit":
            case "--min-size":
            case "--max-size":
                if (value.Equals("off", StringComparison.OrdinalIgnoreCase)) return null;
                return SizeError(value);
            case "--vfs-read-ahead":
            case "--vfs-read-chunk-size":
            case "--buffer-size":
                if (value.Equals("off", StringComparison.OrdinalIgnoreCase)) return null;
                return SizeError(value);
            case "--vfs-cache-max-age":
            case "--dir-cache-time":
            case "--poll-interval":
            case "--contimeout":
            case "--timeout":
            case "--retries-sleep":
                return DurationError(value);
            case "--min-age":
            case "--max-age":
                return DateTimeOffset.TryParseExact(value,
                    ["yyyy-MM-dd", "yyyy-MM-dd'T'HH:mm:ss", "yyyy-MM-dd HH:mm:ss",
                     "yyyy-MM-dd'T'HH:mm:ssK", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK"],
                    CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out _)
                    ? null : DurationError(value);
            case "--bwlimit":
                return BandwidthError(value);
            case "--vfs-read-chunk-streams":
                return IntegerError(value, 0);
            case "--low-level-retries":
            case "--retries":
            case "--transfers":
            case "--checkers":
                return IntegerError(value, 1);
            default:
                return "This option has no supported value format.";
        }
    }

    private static string? IntegerError(string value, int minimum) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number >= minimum
            ? null : $"Enter a whole number of at least {minimum}.";

    private static string? SizeError(string value)
    {
        var match = SizePattern().Match(value);
        if (match.Success && double.TryParse(match.Groups[1].Value, NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out var number) && double.IsFinite(number))
        {
            var unit = match.Groups[2].Value.ToUpperInvariant();
            var power = unit.Length == 0 ? 1 : "BKMGTPE".IndexOf(unit[0]);
            // Some runtimes accept an overflowing float-to-int64 conversion.
            // Keep sizes non-negative and representable rather than inherit it.
            if (number * Math.Pow(1024, power) < long.MaxValue) return null;
        }
        return "Enter a non-negative size such as 512M or 2G within rclone's supported range.";
    }

    private static string? DurationError(string value)
    {
        if (value.Equals("off", StringComparison.Ordinal)) return null;
        if (!DurationPattern().IsMatch(value))
            return "Enter a non-negative duration such as 30s, 5m, 72h, or 7d.";
        decimal nanoseconds = 0;
        try
        {
            foreach (Match part in DurationPartPattern().Matches(value))
            {
                if (!decimal.TryParse(part.Groups[1].Value, NumberStyles.AllowDecimalPoint,
                        CultureInfo.InvariantCulture, out var number)) return "The duration is too large.";
                var multiplier = part.Groups[2].Value switch
                {
                    "ns" => 1m, "us" or "µs" or "μs" => 1_000m, "ms" => 1_000_000m,
                    "m" => 60_000_000_000m, "h" => 3_600_000_000_000m,
                    "d" => 86_400_000_000_000m, "w" => 604_800_000_000_000m,
                    "M" => 2_592_000_000_000_000m, "y" => 31_536_000_000_000_000m,
                    _ => 1_000_000_000m
                };
                nanoseconds = checked(nanoseconds + number * multiplier);
            }
        }
        catch (OverflowException) { return "The duration is too large."; }
        return nanoseconds <= long.MaxValue ? null : "The duration is too large.";
    }

    private static string? BandwidthError(string value)
    {
        const string error = "Enter a bandwidth such as 10M, 10M:5M, or a timetable such as 08:00,10M 18:00,off.";
        if (value.IndexOfAny([' ', ',']) < 0) return ValidBandwidthPair(value) ? null : error;
        var slots = value.Split([' ', ';'], StringSplitOptions.RemoveEmptyEntries);
        if (slots.Length == 0) return error;
        foreach (var slot in slots)
        {
            var match = BandwidthSlotPattern().Match(slot);
            if (!match.Success ||
                !int.TryParse(match.Groups["hour"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var hour) || hour > 23 ||
                !int.TryParse(match.Groups["minute"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var minute) || minute > 59 ||
                !ValidBandwidthPair(match.Groups["rate"].Value)) return error;
        }
        return null;
    }

    private static bool ValidBandwidthPair(string value)
    {
        var parts = value.Split(':');
        return parts.Length is 1 or 2 && parts.All(part =>
            part.Equals("off", StringComparison.OrdinalIgnoreCase) || SizeError(part) is null);
    }

    [GeneratedRegex(@"\A([0-9]+(?:\.[0-9]*)?|\.[0-9]+)([bB]|[kKmMgGtTpPeE](?:[iI]|i[bB])?)?\z", RegexOptions.CultureInvariant)]
    private static partial Regex SizePattern();
    [GeneratedRegex(@"\A(?:(?:\d+(?:\.\d*)?|\.\d+)(?:[dwMy])?|(?:(?:\d+(?:\.\d*)?|\.\d+)(?:ns|us|µs|μs|ms|s|m|h))+)\z", RegexOptions.CultureInvariant)]
    private static partial Regex DurationPattern();
    [GeneratedRegex(@"(\d+(?:\.\d*)?|\.\d+)(ns|us|µs|μs|ms|s|m|h|d|w|M|y)?", RegexOptions.CultureInvariant)]
    private static partial Regex DurationPartPattern();
    [GeneratedRegex(@"\A(?:(?:sun(?:day)?|mon(?:day)?|tue(?:sday)?|wed(?:nesday)?|thu(?:rsday)?|fri(?:day)?|sat(?:urday)?)-)?(?<hour>[0-9]{2}):(?<minute>[0-9]{2}),(?<rate>.+)\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BandwidthSlotPattern();
}
