using System.ComponentModel;
using System.Windows;

namespace ResoDrive.App;

public partial class InstallerPreparationWindow : Window
{
    private bool _finished;

    public InstallerPreparationWindow()
    {
        InitializeComponent();
        Closing += OnClosing;
    }

    internal void SetStage(string message) => Stage.Text = message;

    internal void Finish() => _finished = true;

    internal void ShowFailure(string message)
    {
        _finished = true;
        Heading.Text = "Installation needs attention";
        Stage.Text = message;
        Progress.Visibility = Visibility.Collapsed;
        CloseButton.Visibility = Visibility.Visible;
        CloseButton.Focus();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        e.Cancel = !_finished;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
