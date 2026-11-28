using System.Windows;

namespace ResoDrive.App.Tests;

[Collection(WpfUiTestsGroup.Name)]
public sealed class WindowRestorationTests
{
    [Fact]
    public void PrepareForShow_MakesHiddenMaximizedBackgroundWindowSafeToShow()
    {
        RunInSta(() =>
        {
            var window = new Window
            {
                ShowActivated = false,
                ShowInTaskbar = false,
                WindowState = WindowState.Maximized,
            };

            WindowRestoration.PrepareForShow(window);

            Assert.True(window.ShowActivated);
            Assert.True(window.ShowInTaskbar);
            Assert.Equal(WindowState.Normal, window.WindowState);
            var exception = Record.Exception(window.Show);
            Assert.Null(exception);
            window.Close();
        });
    }

    [Theory]
    [InlineData(WindowState.Normal)]
    [InlineData(WindowState.Minimized)]
    public void PrepareForShow_PreservesNonMaximizedState(WindowState state)
    {
        RunInSta(() =>
        {
            var window = new Window
            {
                ShowActivated = false,
                ShowInTaskbar = false,
                WindowState = state,
            };

            WindowRestoration.PrepareForShow(window);

            Assert.True(window.ShowActivated);
            Assert.True(window.ShowInTaskbar);
            Assert.Equal(state, window.WindowState);
            window.Close();
        });
    }

    private static void RunInSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)));
        if (failure is not null)
            throw failure;
    }
}
