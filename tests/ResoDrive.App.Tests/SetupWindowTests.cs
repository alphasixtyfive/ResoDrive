using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using ResoDrive.Core.Setup;
using ResoDrive.Windows;

namespace ResoDrive.App.Tests;

internal static class SetupWindowTests
{
    // Share the existing STA application fixture. Never show setup or provision a connection.
    internal static void VerifyWithApplicationResources()
    {
        var paths = new ApplicationPaths(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        var editor = new SetupWindow(paths, reservedDriveNames: ["Existing files"]);
        try
        {
            SetField(editor, "_catalog", new SetupProfileCatalog([], ProfileCatalogSource.None));
            SetField(editor, "_manual", true);
            SetField(editor, "_profileId", "manual");
            Control<ComboBox>(editor, "ConnectionTypeBox").SelectedItem = "SFTP";
            Control<TextBox>(editor, "ServerBox").Text = "files.example.test";
            Control<TextBox>(editor, "UsernameBox").Text = "alex";
            Control<PasswordBox>(editor, "PasswordBox").Password = "test-password";
            Control<TextBox>(editor, "DisplayNameBox").Text = "Files";
            var drive = Control<ComboBox>(editor, "DriveBox");
            drive.Items.Add('S');
            drive.SelectedItem = 'S';

            var port = Control<TextBox>(editor, "PortBox");
            foreach (var invalid in new[] { "x", "+", "1.5", "22\n", "٢٢" })
            {
                port.SelectAll();
                var typed = new System.Windows.Input.TextCompositionEventArgs(
                    System.Windows.Input.InputManager.Current.PrimaryKeyboardDevice,
                    new System.Windows.Input.TextComposition(System.Windows.Input.InputManager.Current, port, invalid))
                {
                    RoutedEvent = System.Windows.Input.TextCompositionManager.PreviewTextInputEvent,
                };
                port.RaiseEvent(typed);
                Assert.True(typed.Handled);
                var paste = new DataObjectPastingEventArgs(new DataObject(DataFormats.UnicodeText, invalid),
                    false, DataFormats.UnicodeText) { RoutedEvent = DataObject.PastingEvent };
                port.RaiseEvent(paste);
                Assert.True(paste.CommandCancelled);
            }
            port.SelectAll();
            var validPaste = new DataObjectPastingEventArgs(new DataObject(DataFormats.UnicodeText, "65535"),
                false, DataFormats.UnicodeText) { RoutedEvent = DataObject.PastingEvent };
            port.RaiseEvent(validPaste);
            Assert.False(validPaste.CommandCancelled);
            foreach (var invalid in new[] { "", "0", "65536", "-1", "+22", "22.0", "abc", "22\n", "999999999999999999" })
            {
                port.Text = invalid;
                AssertInvalid(editor, "port");
                Assert.True(Control<Button>(editor, "CancelButton").IsEnabled);
            }
            var advanced = Control<Expander>(editor, "AdvancedBox");
            editor.Measure(new System.Windows.Size(520, 380));
            editor.Arrange(new Rect(0, 0, 520, 380));
            editor.UpdateLayout();
            AssertInvalid(editor, "port");
            var portError = Control<TextBlock>(editor, "StatusText").Text;
            advanced.IsExpanded = true;
            advanced.IsExpanded = false;
            Assert.Equal(portError, Control<TextBlock>(editor, "StatusText").Text);
            foreach (var valid in new[] { "1", "22", "65535" })
            {
                port.Text = valid;
                Assert.True(editor.TryPrepareSetup(out var catalog, out var request));
                var sftp = Assert.IsType<SftpConnectionDefinition>(Assert.Single(catalog!.Profiles).Connection);
                Assert.Equal(int.Parse(valid, System.Globalization.CultureInfo.InvariantCulture), sftp.Port);
                Assert.Equal('S', request!.DriveLetter);
            }
            port.Text = "22";
            var name = Control<TextBox>(editor, "DisplayNameBox");
            foreach (var invalid in new[] { "", new string('x', 129), "Invalid\tname", "Files\n" })
            {
                name.Text = invalid;
                AssertInvalid(editor, invalid.Length > 128 ? "128" : invalid.Any(char.IsControl) ? "control characters" : "display name");
            }
            name.Text = " existing FILES ";
            AssertInvalid(editor, "already exists");
            name.Text = "Files";
            var username = Control<TextBox>(editor, "UsernameBox");
            foreach (var invalid in new[] { "", new string('u', 257), "bad\tusername", "alex\n" })
            {
                username.Text = invalid;
                AssertInvalid(editor, "username");
            }
            username.Text = "alex";
            var remote = Control<TextBox>(editor, "RemotePathBox");
            foreach (var invalid in new[] { "one//two", "../private", "one\\two", "one\ttwo", "Shared\n", new string('x', 2049) })
            {
                remote.Text = invalid;
                Assert.False(editor.TryPrepareSetup(out _, out _));
            }
            remote.Text = "/Shared/";
            Assert.True(editor.TryPrepareSetup(out _, out var normalized));
            Assert.Equal("/Shared/", normalized!.RemotePath); // Existing plan applies benign trailing-slash normalization.
            remote.Text = string.Empty;
            var server = Control<TextBox>(editor, "ServerBox");
            foreach (var invalid in new[] { "files.example.test:22", "user@files.example.test", "files.example.test/path", "files.example.test\n" })
            {
                server.Text = invalid;
                AssertInvalid(editor, invalid.Any(char.IsControl) ? "control characters" : "host");
            }

            Control<ComboBox>(editor, "ConnectionTypeBox").SelectedItem = "WebDAV";
            foreach (var invalid in new[] { "http://files.example.test/", "https://user:pass@files.example.test/", "https://files.example.test/?secret=1", "https://files.example.test/#anchor", "https://files.exa\nmple.test/", "https:\\files.example.test\\shared" })
            {
                server.Text = invalid;
                Assert.False(editor.TryPrepareSetup(out _, out _));
            }
            server.Text = "https://files.example.test:8443/shared/";
            port.Text = "irrelevant hidden value";
            Assert.True(editor.TryPrepareSetup(out var webCatalog, out _));
            var webDav = Assert.IsType<WebDavConnectionDefinition>(Assert.Single(webCatalog!.Profiles).Connection);
            Assert.Equal(8443, webDav.BaseUrl.Port);
            Assert.Equal("/shared/", webDav.PathTemplate);

            var password = Control<PasswordBox>(editor, "PasswordBox");
            foreach (var invalid in new[] { "", "bad\npassword", new string('p', 2049) })
            {
                password.Password = invalid;
                AssertInvalid(editor, "password");
            }
            password.Password = "restored-password";
            Assert.Empty(Control<TextBlock>(editor, "StatusText").Text);
            Assert.True(editor.TryPrepareSetup(out _, out _));

            Control<ComboBox>(editor, "ConnectionTypeBox").SelectedItem = "Nextcloud";
            server.Text = "https://files.example.test/subfolder/";
            AssertInvalid(editor, "only the server address");
            server.Text = "https://files.example.test/";
            Assert.True(editor.TryPrepareSetup(out _, out _));

            var options = Control<ResoDrive.App.Controls.MountOptionsControl>(editor, "OptionsEditor");
            var arguments = Control<TextBox>(options, "ArgumentsBox");
            arguments.Text = string.Join(Environment.NewLine,
                Enumerable.Range(0, 64).Select(index => $"--not-supported-{index}"));
            Assert.False(editor.TryPrepareSetup(out _, out _));
            var optionsError = Control<TextBlock>(editor, "StatusText").Text;
            Assert.True(optionsError.Length < 120);
            advanced.IsExpanded = false;
            options.Measure(new System.Windows.Size(350, 200));
            options.Arrange(new Rect(0, 0, 350, 200));
            options.UpdateLayout();
            advanced.IsExpanded = true;
            Assert.Equal(optionsError, Control<TextBlock>(editor, "StatusText").Text);
            arguments.Text = "--timeout=1m";
            Assert.Empty(Control<TextBlock>(editor, "StatusText").Text);
            Assert.True(editor.TryPrepareSetup(out _, out _));

            drive.SelectedItem = null;
            AssertInvalid(editor, "drive letter");
            Assert.False(Directory.Exists(paths.Root));
        }
        finally { editor.Close(); }
    }

    private static void AssertInvalid(SetupWindow editor, string message)
    {
        Assert.False(editor.TryPrepareSetup(out var catalog, out var request));
        Assert.Null(catalog);
        Assert.Null(request);
        Assert.Contains(message, Control<TextBlock>(editor, "StatusText").Text, StringComparison.OrdinalIgnoreCase);
        Assert.Null(editor.Result);
    }

    private static T Control<T>(FrameworkElement owner, string name) where T : FrameworkElement =>
        Assert.IsType<T>(owner.FindName(name));

    private static void SetField(SetupWindow editor, string name, object value) =>
        typeof(SetupWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(editor, value);
}
