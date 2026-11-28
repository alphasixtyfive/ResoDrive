using System.Runtime.ExceptionServices;
using System.Windows;

namespace ResoDrive.App.Tests;

[Collection(WpfUiTestsGroup.Name)]
public sealed class WindowAppearanceTests
{
    [Fact]
    public void PrepareDialog_RevealsWindowOnlyAfterFirstContentRender()
    {
        RunOnStaThread(() =>
        {
            var window = new TestWindow { Opacity = 0.82 };

            WindowAppearance.PrepareDialog(window);

            Assert.Equal(0, window.Opacity);
            window.RaiseContentRendered();
            Assert.Equal(0.82, window.Opacity);

            window.Opacity = 0.5;
            window.RaiseContentRendered();
            Assert.Equal(0.5, window.Opacity);
        });
    }

    private static void RunOnStaThread(Action action)
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
        thread.Join();
        if (failure is not null)
            ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private sealed class TestWindow : Window
    {
        internal void RaiseContentRendered() => OnContentRendered(EventArgs.Empty);
    }
}
