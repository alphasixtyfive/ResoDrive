using System.Globalization;

namespace ResoDrive.App;

internal static class NumericInput
{
    // Empty is a valid editing state, not a valid saved value. Do not truncate
    // pasted numbers: a length cap can silently turn 1000 into 100.
    internal static bool IsAsciiDigits(string text) => text.All(character => character is >= '0' and <= '9');

    internal static string ReplaceSelection(string text, int selectionStart, int selectionLength, string insertion) =>
        text.Remove(selectionStart, selectionLength).Insert(selectionStart, insertion);

    internal static bool TryGetPositiveInteger(string text, int maximum, out int value) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value) && value >= 1 && value <= maximum;
}
