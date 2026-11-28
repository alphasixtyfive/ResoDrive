using System.Windows;

namespace ResoDrive.App;

internal partial class MigrationStartupWindow : Window
{
    private bool _canClose;

    internal MigrationStartupWindow()
    {
        InitializeComponent();
        Closing += (_, e) => e.Cancel = !_canClose;
        SourceInitialized += (_, _) => WindowAppearance.ApplyDarkTitleBar(this);
    }

    internal void ShowFailure(string message)
    {
        Detail.Text = "The program update was installed, but your data could not be moved. " +
            message + " Your existing data has been kept. Close ResoDrive and try again.";
        Progress.Visibility = Visibility.Collapsed;
        CloseButton.Visibility = Visibility.Visible;
        _canClose = true;
        CloseButton.Focus();
    }

    internal void Finish() { _canClose = true; Close(); }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
