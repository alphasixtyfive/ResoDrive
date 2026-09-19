using System.Diagnostics;
using System.Windows;
using WpfMessageBox = ResoDrive.App.ModernMessageBox;
using WpfWindow = System.Windows.Window;

namespace ResoDrive.App;

public partial class AboutWindow : WpfWindow
{
    public AboutWindow()
    {
        InitializeComponent();
        VersionText.Text = $"Version {ProductInfo.Version}";
        WindowAppearance.PrepareDialog(this);
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != System.Windows.Input.Key.Escape) return;
            e.Handled = true;
            Close();
        };
    }

    private void OpenLink(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        e.Handled = true;
        try
        {
            using var process = Process.Start(
                new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }
            );
        }
        catch (Exception exception)
            when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            WpfMessageBox.Show(
                this,
                exception.Message,
                "Could not open the link",
                MessageBoxButton.OK,
                MessageBoxImage.Error
            );
        }
    }
}
