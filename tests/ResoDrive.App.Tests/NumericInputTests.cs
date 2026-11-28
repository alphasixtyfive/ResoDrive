namespace ResoDrive.App.Tests;

public sealed class NumericInputTests
{
    [Theory]
    [InlineData("1", true)]
    [InlineData("100", true)]
    [InlineData("0", false)]
    [InlineData("101", false)]
    [InlineData("1000", false)]
    [InlineData("2147483648", false)]
    [InlineData("", false)]
    [InlineData("Unlimited", false)]
    [InlineData(" 5", false)]
    [InlineData("5 ", false)]
    [InlineData("+5", false)]
    [InlineData("-5", false)]
    [InlineData("1.5", false)]
    [InlineData("١", false)]
    public void SavedNumbersRequireWholePositiveValuesWithinTheLimit(string text, bool valid) =>
        Assert.Equal(valid, NumericInput.TryGetPositiveInteger(text, 100, out _));

    [Theory]
    [InlineData("", true)]
    [InlineData("1000", true)]
    [InlineData("Unlimited456", false)]
    [InlineData("5\n", false)]
    [InlineData("１２", false)]
    public void EditingAllowsClearingAndCompleteNumbersWithoutTruncation(string text, bool valid) =>
        Assert.Equal(valid, NumericInput.IsAsciiDigits(text));

    [Fact]
    public void InsertionValidatesTheWholeProposedTextAndSupportsSelectionReplacement()
    {
        Assert.Equal("1000", NumericInput.ReplaceSelection("5", 0, 1, "1000"));
        Assert.Equal("17", NumericInput.ReplaceSelection("123", 1, 2, "7"));
        Assert.Equal("", NumericInput.ReplaceSelection("37", 0, 2, ""));
        Assert.False(NumericInput.IsAsciiDigits(NumericInput.ReplaceSelection("12", 1, 0, "x")));
    }
}
