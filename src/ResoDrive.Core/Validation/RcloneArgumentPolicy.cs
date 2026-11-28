namespace ResoDrive.Core.Validation;

public static class RcloneArgumentPolicy
{
    private const int MaximumTokenCount = 64;
    private const int MaximumTokenLength = 2_048;
    private const int MaximumTotalLength = 8_192;

    private static readonly Dictionary<string, OptionDefinition> MountOptions =
        CreateOptions(
            values:
            [
                "--buffer-size", "--bwlimit", "--checkers", "--contimeout", "--dir-cache-time",
                "--low-level-retries", "--poll-interval", "--retries", "--timeout", "--transfers",
                "--retries-sleep",
                "--vfs-cache-max-age", "--vfs-cache-max-size", "--vfs-cache-mode",
                "--vfs-read-chunk-size", "--vfs-read-ahead", "--vfs-read-chunk-size-limit",
                "--vfs-read-chunk-streams", "--vfs-cache-min-free-space"
            ],
            switches: ["--case-insensitive", "--links", "--network-mode", "--read-only"]);

    private static readonly Dictionary<string, OptionDefinition> SyncOptions =
        CreateOptions(
            values:
            [
                "--bwlimit", "--checkers", "--contimeout", "--low-level-retries", "--max-age",
                "--max-size", "--min-age", "--min-size", "--retries", "--timeout", "--transfers"
            ],
            switches:
            [
                "--checksum", "--ignore-existing", "--ignore-times", "--immutable", "--size-only",
                "--update"
            ]);

    private static readonly HashSet<string> ManagerOwnedOptions = new(StringComparer.OrdinalIgnoreCase)
    {
        "--ask-password", "--cache-dir", "--config", "--dry-run", "--log-file", "--log-level", "--use-json-log",
        "--no-console", "--password-command", "--stats", "--volname"
    };

    private static readonly HashSet<string> ExternalCommandOptions = new(StringComparer.OrdinalIgnoreCase)
    {
        "--metadata-mapper", "--password-command"
    };

    public static ValidationResult ValidateMount(IReadOnlyList<string>? arguments) =>
        Validate(arguments, MountOptions, "arguments");

    public static ValidationResult ValidateSync(IReadOnlyList<string>? arguments) =>
        Validate(arguments, SyncOptions, "arguments");

    private static ValidationResult Validate(
        IReadOnlyList<string>? arguments,
        Dictionary<string, OptionDefinition> allowedOptions,
        string field)
    {
        var issues = new List<ValidationIssue>();
        if (arguments is null)
        {
            issues.Add(new("arguments.null", "The argument collection cannot be null.", field));
            return new ValidationResult(issues);
        }

        if (arguments.Count > MaximumTokenCount)
        {
            issues.Add(new(
                "arguments.tooMany",
                $"No more than {MaximumTokenCount} argument tokens are allowed.",
                field));
        }

        var totalLength = 0;
        var seenOptions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < arguments.Count; index++)
        {
            var token = arguments[index];
            totalLength += token?.Length ?? 0;
            if (string.IsNullOrWhiteSpace(token))
            {
                issues.Add(new("arguments.emptyToken", "Argument tokens cannot be empty.", $"{field}[{index}]"));
                continue;
            }

            if (token.Length > MaximumTokenLength)
            {
                issues.Add(new(
                    "arguments.tokenTooLong",
                    $"An argument token cannot exceed {MaximumTokenLength} characters.",
                    $"{field}[{index}]"));
            }

            if (token.Any(IsUnsafeCharacter))
            {
                issues.Add(new(
                    "arguments.controlCharacter",
                    "Argument tokens cannot contain control characters.",
                    $"{field}[{index}]"));
                continue;
            }

            if (token == "--")
            {
                issues.Add(new(
                    "arguments.terminator",
                    "The end-of-options token is not allowed.",
                    $"{field}[{index}]"));
                continue;
            }

            if (!token.StartsWith("--", StringComparison.Ordinal))
            {
                issues.Add(new(
                    "arguments.positional",
                    "Only long-form rclone options are allowed; positional arguments are managed by the application.",
                    $"{field}[{index}]"));
                continue;
            }

            var separator = token.IndexOf('=');
            var optionName = separator < 0 ? token : token[..separator];
            var optionLabel = DescribeOption(optionName);
            var inlineValue = separator < 0 ? null : token[(separator + 1)..];

            if (IsRemoteControlOption(optionName))
            {
                issues.Add(new("arguments.remoteControl", $"{optionLabel} is not allowed.", $"{field}[{index}]"));
                continue;
            }

            if (IsDumpOption(optionName))
            {
                issues.Add(new("arguments.dump", $"{optionLabel} is not allowed because it can expose sensitive data.", $"{field}[{index}]"));
                continue;
            }

            if (ExternalCommandOptions.Contains(optionName))
            {
                issues.Add(new("arguments.externalCommand", $"{optionLabel} is not allowed to execute external commands.", $"{field}[{index}]"));
                continue;
            }

            if (ManagerOwnedOptions.Contains(optionName))
            {
                issues.Add(new("arguments.managerOwned", $"{optionLabel} is managed by the application.", $"{field}[{index}]"));
                continue;
            }

            if (!allowedOptions.TryGetValue(optionName, out var definition))
            {
                issues.Add(new("arguments.unsupported", $"{optionLabel} is not an approved option.", $"{field}[{index}]"));
                continue;
            }

            if (!seenOptions.Add(optionName))
            {
                issues.Add(new("arguments.duplicate", $"{optionLabel} cannot be specified more than once.", $"{field}[{index}]"));
            }

            if (definition.RequiresValue)
            {
                if (inlineValue is not null)
                {
                    if (inlineValue.Length == 0)
                    {
                        issues.Add(new("arguments.missingValue", $"{optionLabel} requires a value.", $"{field}[{index}]"));
                    }
                    else
                    {
                        ValidateValue(inlineValue, field, index, issues, optionName);
                    }
                }
                else if (index + 1 >= arguments.Count || LooksLikeOption(arguments[index + 1]))
                {
                    issues.Add(new("arguments.missingValue", $"{optionLabel} requires a value.", $"{field}[{index}]"));
                }
                else
                {
                    var value = arguments[++index];
                    totalLength += value?.Length ?? 0;
                    ValidateValue(value, field, index, issues, optionName);
                }
            }
            else if (inlineValue is not null)
            {
                issues.Add(new(
                    "arguments.unexpectedValue",
                    $"{optionLabel} is a switch and does not accept a value.",
                    $"{field}[{index}]"));
            }
        }

        if (totalLength > MaximumTotalLength)
        {
            issues.Add(new(
                "arguments.tooLong",
                $"The combined argument length cannot exceed {MaximumTotalLength} characters.",
                field));
        }

        return issues.Count == 0 ? ValidationResult.Valid : new ValidationResult(issues);
    }

    private static void ValidateValue(
        string? value,
        string field,
        int index,
        List<ValidationIssue> issues,
        string optionName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            issues.Add(new("arguments.missingValue", "Option values cannot be empty.", $"{field}[{index}]"));
            return;
        }

        if (value.Length > MaximumTokenLength)
        {
            issues.Add(new(
                "arguments.tokenTooLong",
                $"An argument token cannot exceed {MaximumTokenLength} characters.",
                $"{field}[{index}]"));
        }

        if (value.Length <= MaximumTokenLength && RcloneOptionValue.Error(optionName, value) is { } error)
        {
            issues.Add(new("arguments.invalidValue", $"{optionName}: {error}", $"{field}[{index}]"));
        }
        if (value.Any(IsUnsafeCharacter))
        {
            issues.Add(new(
                "arguments.controlCharacter",
                "Argument tokens cannot contain control characters.",
                $"{field}[{index}]"));
        }
    }

    private static bool LooksLikeOption(string? value) =>
        value is null || (value.Length > 0 && value[0] == '-');

    private static string DescribeOption(string name) => name.Length <= 32 ? name : name[..29] + "…";

    private static bool IsUnsafeCharacter(char value) => value == '\0' || value == '\r' || value == '\n' || char.IsControl(value);

    private static bool IsRemoteControlOption(string optionName) =>
        optionName.Equals("--rc", StringComparison.OrdinalIgnoreCase) ||
        optionName.StartsWith("--rc-", StringComparison.OrdinalIgnoreCase);

    private static bool IsDumpOption(string optionName) =>
        optionName.Equals("--dump", StringComparison.OrdinalIgnoreCase) ||
        optionName.StartsWith("--dump-", StringComparison.OrdinalIgnoreCase);

    private static Dictionary<string, OptionDefinition> CreateOptions(
        IEnumerable<string> values,
        IEnumerable<string> switches)
    {
        var options = new Dictionary<string, OptionDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in values)
        {
            options.Add(value, new OptionDefinition(true));
        }

        foreach (var value in switches)
        {
            options.Add(value, new OptionDefinition(false));
        }

        return options;
    }

    private sealed record OptionDefinition(bool RequiresValue);
}
