namespace ResoDrive.App.Tests;

// Native windows, WPF resources, and rendering share process state even on
// separate STA threads. Keep UI fixtures together while model tests stay parallel.
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class WpfUiTestsGroup
{
    public const string Name = "WPF UI";
}
