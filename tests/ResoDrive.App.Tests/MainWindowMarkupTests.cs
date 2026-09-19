using System.Xml.Linq;

namespace ResoDrive.App.Tests;

public sealed class MainWindowMarkupTests
{
    [Fact]
    public void MessageDialogSeverityGlyphUsesCircularBadge()
    {
        var document = Load("MessageDialog.xaml");
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

        var badge = Assert.Single(document.Descendants(presentation + "Border"), element =>
            (string?)element.Attribute(xaml + "Name") == "SeverityBadge");
        Assert.Equal("32", (string?)badge.Attribute("Width"));
        Assert.Equal("32", (string?)badge.Attribute("Height"));
        Assert.Equal("16", (string?)badge.Attribute("CornerRadius"));
        Assert.Equal("{DynamicResource ControlBrush}", (string?)badge.Attribute("Background"));
        Assert.Equal("{DynamicResource BorderHoverBrush}", (string?)badge.Attribute("BorderBrush"));
        Assert.Contains(badge.Descendants(presentation + "TextBlock"), element =>
            (string?)element.Attribute(xaml + "Name") == "SeverityGlyph");
    }

    [Fact]
    public void SyncRowsUseSingleBaselineStatusFooterWithoutProgressBars()
    {
        var document = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "Fixtures", "MainWindow.xaml"));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

        var bindings = document
            .Descendants()
            .Attributes("Text")
            .Select(attribute => attribute.Value)
            .ToArray();

        var syncRowTemplate = Assert.Single(
            document.Descendants(presentation + "DataTemplate"),
            template => template.Descendants().Attributes("Text")
                .Any(attribute => attribute.Value == "{Binding StatusLine}"));
        Assert.Empty(syncRowTemplate.Descendants(presentation + "ProgressBar"));
        Assert.Contains("{Binding StatusLine}", bindings);
        Assert.DoesNotContain("{Binding StatusPrimary}", bindings);
        Assert.DoesNotContain("{Binding StatusSecondary}", bindings);
    }

    [Fact]
    public void DriveAndSyncRowsUseTheSameSettingsActionTreatment()
    {
        var document = Load("MainWindow.xaml");
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

        var settingsButtons = document.Descendants(presentation + "Button")
            .Where(button => (string?)button.Attribute("Click") is "Options_Click" or "JobOptions_Click")
            .ToArray();

        Assert.Equal(2, settingsButtons.Length);
        Assert.All(settingsButtons, button =>
        {
            Assert.Equal("{StaticResource IconOnlyButton}", (string?)button.Attribute("Style"));
            Assert.Equal("\uE713", (string?)button.Attribute("Content"));
            Assert.Equal("{Binding OptionsAccessibleName}",
                (string?)button.Attribute("AutomationProperties.Name"));
        });
        Assert.Equal("Drive settings", (string?)settingsButtons[0].Attribute("ToolTip"));
        Assert.Equal("Sync settings", (string?)settingsButtons[1].Attribute("ToolTip"));
    }

    [Fact]
    public void StatusRailRadiusFitsItsThreePixelWidth()
    {
        var document = Load("Controls.xaml");
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        var style = Assert.Single(
            document.Descendants(presentation + "Style"),
            element => (string?)element.Attribute(xaml + "Key") == "RowStatusRail");
        var radius = Assert.Single(
            style.Elements(presentation + "Setter"),
            element => (string?)element.Attribute("Property") == "CornerRadius")
            .Attribute("Value")?.Value;

        Assert.NotNull(radius);
        Assert.True(double.Parse(radius.Split(',')[0], System.Globalization.CultureInfo.InvariantCulture) <= 1.5);
    }

    [Fact]
    public void FieldTemplatesApplyTheirSemanticPadding()
    {
        var document = Load("Controls.xaml");
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        var contentHosts = document.Descendants(presentation + "ScrollViewer")
            .Where(element => (string?)element.Attribute(xaml + "Name") == "PART_ContentHost")
            .ToArray();

        Assert.Equal(2, contentHosts.Count(host =>
            (string?)host.Attribute("Padding") == "{TemplateBinding Padding}"));
    }

    [Fact]
    public void WelcomeIsCompactAndHasThreePagesWithoutScrollbars()
    {
        var document = Load("WelcomeWindow.xaml");
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

        Assert.Empty(document.Descendants(presentation + "ScrollViewer"));
        foreach (var name in new[] { "DrivePage", "SyncPage", "RclonePage" })
        {
            Assert.Single(document.Descendants(), element =>
                (string?)element.Attribute(xaml + "Name") == name);
        }
    }

    [Fact]
    public void SetupUsesOneResponsiveScrollSurfaceAndSharedArgumentEditor()
    {
        var document = Load("SetupWindow.xaml");
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

        Assert.Single(document.Descendants(presentation + "ScrollViewer"));
        XNamespace controls = "clr-namespace:ResoDrive.App.Controls";
        Assert.Single(document.Descendants(controls + "MountOptionsControl"));
        var editor = Load("MountEditorWindow.xaml");
        Assert.Single(editor.Descendants(controls + "MountOptionsControl"));
        Assert.Single(document.Descendants(), element =>
            (string?)element.Attribute(xaml + "Name") == "ProfilePanel");
    }

    [Fact]
    public void SetupDoesNotPrefillRequiredUserTextFields()
    {
        var document = Load("SetupWindow.xaml");
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

        foreach (var name in new[] { "UsernameBox", "DisplayNameBox" })
        {
            var field = Assert.Single(document.Descendants(presentation + "TextBox"), element =>
                (string?)element.Attribute(xaml + "Name") == name);
            Assert.True(string.IsNullOrEmpty((string?)field.Attribute("Text")));
        }

        var port = Assert.Single(document.Descendants(presentation + "TextBox"), element =>
            (string?)element.Attribute(xaml + "Name") == "PortBox");
        Assert.Equal("22", (string?)port.Attribute("Text"));
    }

    [Fact]
    public void SettingsExposeOneContextualUpdateActionPerComponent()
    {
        var document = Load("MainWindow.xaml");
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

        var actionNames = new[] { "ApplicationUpdateActionButton", "UpdateRcloneButton" };
        foreach (var name in actionNames)
        {
            var button = Assert.Single(document.Descendants(presentation + "Button"), element =>
                (string?)element.Attribute(xaml + "Name") == name);
            Assert.Equal("34", (string?)button.Attribute("Width"));
            Assert.Equal("34", (string?)button.Attribute("MinWidth"));
            Assert.Equal("{StaticResource IconOnlyButton}", (string?)button.Attribute("Style"));
            Assert.Equal("Check for updates", (string?)button.Attribute("ToolTip"));
            Assert.Equal("Check for updates", (string?)button.Attribute("AutomationProperties.Name"));
            Assert.Null(button.Attribute("Visibility"));
            Assert.Contains(button.Descendants(presentation + "TextBlock"), element =>
                (string?)element.Attribute("Text") == "\uE72C" &&
                (string?)element.Attribute("Margin") == "0");
        }

        var controls = Load("Controls.xaml");
        var iconOnlyStyle = Assert.Single(controls.Descendants(presentation + "Style"), element =>
            (string?)element.Attribute(xaml + "Key") == "IconOnlyButton");
        Assert.Equal("34", SetterValue(iconOnlyStyle, presentation, "Width"));
        Assert.Equal("0", SetterValue(iconOnlyStyle, presentation, "Padding"));

        var baseButtonStyle = Assert.Single(controls.Descendants(presentation + "Style"), element =>
            (string?)element.Attribute(xaml + "Key") == "ButtonStyle");
        Assert.Equal("32", SetterValue(baseButtonStyle, presentation, "MinHeight"));

        Assert.DoesNotContain(document.Descendants(presentation + "Button"), element =>
            ((string?)element.Attribute(xaml + "Name"))?.StartsWith("Check", StringComparison.Ordinal) == true);

        Assert.Single(document.Descendants(presentation + "Path"), element =>
            (string?)element.Attribute(xaml + "Name") == "ApplicationUpdateStatusIcon");
        var applicationAction = Assert.Single(document.Descendants(presentation + "Button"), element =>
            (string?)element.Attribute(xaml + "Name") == "ApplicationUpdateActionButton");
        Assert.Equal("ApplicationUpdateAction_Click", (string?)applicationAction.Attribute("Click"));
        Assert.Contains(applicationAction.Descendants(presentation + "TextBlock"), element =>
            (string?)element.Attribute("Text") == "Update");

        var applicationStatus = Assert.Single(document.Descendants(presentation + "TextBlock"), element =>
            (string?)element.Attribute(xaml + "Name") == "ApplicationUpdateStatusText");
        Assert.Equal("{StaticResource ComponentStatusText}", (string?)applicationStatus.Attribute("Style"));
        Assert.Equal("Polite", (string?)applicationStatus.Attribute("AutomationProperties.LiveSetting"));

        var rcloneStatus = Assert.Single(document.Descendants(presentation + "TextBlock"), element =>
            (string?)element.Attribute(xaml + "Name") == "RcloneStatusText");
        Assert.Equal("{StaticResource ComponentStatusText}", (string?)rcloneStatus.Attribute("Style"));
        Assert.Equal("Polite", (string?)rcloneStatus.Attribute("AutomationProperties.LiveSetting"));
        Assert.DoesNotContain(document.Descendants(presentation + "TextBlock"), element =>
            (string?)element.Attribute(xaml + "Name") == "RcloneUpdateStatusText");

        var componentStatusStyle = Assert.Single(controls.Descendants(presentation + "Style"), element =>
            (string?)element.Attribute(xaml + "Key") == "ComponentStatusText");
        Assert.Equal("CharacterEllipsis", SetterValue(componentStatusStyle, presentation, "TextTrimming"));
        Assert.Equal("0,-3,16,0", SetterValue(componentStatusStyle, presentation, "Margin"));
    }

    [Fact]
    public void DriveRowsExposeConnectionTypeBadge()
    {
        var document = Load("MainWindow.xaml");
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

        var badgeText = Assert.Single(document.Descendants(presentation + "TextBlock"), element =>
            (string?)element.Attribute("Text") == "{Binding ConnectionTypeDisplay}");
        Assert.Equal("{StaticResource ConnectionBadgeText}", (string?)badgeText.Attribute("Style"));

        var badge = Assert.IsType<XElement>(badgeText.Parent);
        Assert.Equal("3", (string?)badge.Attribute("Grid.Column"));
        Assert.Equal("{StaticResource ConnectionBadge}", (string?)badge.Attribute("Style"));
        Assert.Equal("{Binding ConnectionTypeDisplay}", (string?)badge.Attribute("AutomationProperties.Name"));

        var controls = Load("Controls.xaml");
        var badgeStyle = Assert.Single(controls.Descendants(presentation + "Style"), element =>
            (string?)element.Attribute(xaml + "Key") == "ConnectionBadge");
        Assert.Equal("58", SetterValue(badgeStyle, presentation, "Width"));
        Assert.Equal("34", SetterValue(badgeStyle, presentation, "Height"));
        Assert.Equal("8,0", SetterValue(badgeStyle, presentation, "Padding"));
        Assert.Equal("6", SetterValue(badgeStyle, presentation, "CornerRadius"));
        Assert.Equal("Center", SetterValue(badgeStyle, presentation, "VerticalAlignment"));

        var protocolTriggers = badgeStyle.Descendants(presentation + "DataTrigger")
            .Where(element => (string?)element.Attribute("Binding") == "{Binding ConnectionTypeDisplay}")
            .Select(element => (string?)element.Attribute("Value"))
            .ToArray();
        Assert.Contains("WebDAV", protocolTriggers);
        Assert.Contains("SFTP", protocolTriggers);

        var badgeTextStyle = Assert.Single(controls.Descendants(presentation + "Style"), element =>
            (string?)element.Attribute(xaml + "Key") == "ConnectionBadgeText");
        Assert.Equal("Center", SetterValue(badgeTextStyle, presentation, "VerticalAlignment"));

          var host = Assert.Single(document.Descendants(presentation + "TextBlock"), element =>
              element.Elements(presentation + "Run").Any(run =>
                  (string?)run.Attribute("Text") == "{Binding ConnectionHostDisplay, Mode=OneWay}"));
        Assert.Equal("CharacterEllipsis", (string?)host.Attribute("TextTrimming"));
        Assert.Null(host.Attribute("MaxWidth"));
    }

    [Fact]
    public void AboutWindowExposesAccessibleRepositoryLinks()
    {
        var document = Load("AboutWindow.xaml");
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

        var actions = document.Descendants(presentation + "Hyperlink")
            .Select(link => (string?)link.Attribute("NavigateUri"))
            .Where(value => value is not null)
            .ToArray();

        Assert.Contains("{x:Static local:ProductInfo.RepositoryPage}", actions);
        Assert.Contains("{x:Static local:ProductInfo.ReleasesPage}", actions);
        Assert.Contains("{x:Static local:ProductInfo.IssuesPage}", actions);
        Assert.Contains("{x:Static local:ProductInfo.LicensePage}", actions);
        Assert.DoesNotContain(document.Descendants(presentation + "Button"), button =>
            (string?)button.Attribute("Content") == "Close");

        var subtitle = Assert.Single(document.Descendants(presentation + "TextBlock"), element =>
            (string?)element.Attribute("Text") == "Mount Nextcloud, WebDAV, and SFTP storage as Windows drives.");
        Assert.Equal("Wrap", (string?)subtitle.Attribute("TextWrapping"));
    }

    [Fact]
    public void MainPagesExposeActionableEmptyStates()
    {
        var document = Load("MainWindow.xaml");
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

        foreach (var heading in new[] { "No drives yet", "No sync jobs yet", "No recent activity" })
        {
            Assert.Single(document.Descendants(presentation + "TextBlock"), element =>
                (string?)element.Attribute("Text") == heading);
        }

        var emptyStateActions = document.Descendants(presentation + "Button")
            .Where(button => ((string?)button.Attribute("AutomationProperties.Name"))?.Contains(
                "your first", StringComparison.Ordinal) == true ||
                (string?)button.Attribute("AutomationProperties.Name") == "Open log folder")
            .Select(button => (string?)button.Attribute("Click"))
            .ToArray();
        Assert.Contains("AddMount_Click", emptyStateActions);
        Assert.Contains("NewJob_Click", emptyStateActions);
        Assert.Contains("OpenLogs_Click", emptyStateActions);
    }

    [Fact]
    public void HostRecoveryAndOperationFeedbackAreAccessible()
    {
        var document = Load("MainWindow.xaml");
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

        var banner = Assert.Single(document.Descendants(presentation + "Border"), element =>
            (string?)element.Attribute(xaml + "Name") == "HostRecoveryBanner");
        Assert.Equal("Assertive", (string?)banner.Attribute("AutomationProperties.LiveSetting"));
        var retry = Assert.Single(banner.Descendants(presentation + "Button"));
        Assert.Equal("RetryHost_Click", (string?)retry.Attribute("Click"));
        Assert.Equal("Retry background host connection", (string?)retry.Attribute("AutomationProperties.Name"));

        Assert.DoesNotContain(document.Descendants(presentation + "TextBlock"), element =>
            (string?)element.Attribute(xaml + "Name") == "OperationStatusText");
    }

    [Fact]
    public void RecoveryToolsExposeNamedSettingsActions()
    {
        var document = Load("MainWindow.xaml");
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

        var actions = document.Descendants(presentation + "Button")
            .Where(button => (string?)button.Attribute("AutomationProperties.Name") is
                "Export settings" or "Import settings")
            .ToDictionary(
                button => (string)button.Attribute("AutomationProperties.Name")!,
                button => (string?)button.Attribute("Click"),
                StringComparer.Ordinal);

        Assert.Equal("ExportSettings_Click", actions["Export settings"]);
        Assert.Equal("ImportSettings_Click", actions["Import settings"]);
        Assert.DoesNotContain(document.Descendants(presentation + "Button"), button =>
            string.Equals((string?)button.Attribute("AutomationProperties.Name"),
                "Create diagnostics bundle", StringComparison.Ordinal));

        var recoveryCopy = document.Descendants(presentation + "TextBlock")
            .Select(element => (string?)element.Attribute("Text"))
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .ToArray();
        Assert.DoesNotContain(recoveryCopy, text =>
            text!.Contains("privacy-safe", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(recoveryCopy, text =>
            text!.Contains("import", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void SharedRowAndDestructiveStylesCoverEditors()
    {
        var controls = Load("Controls.xaml");
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        var keys = controls.Descendants(presentation + "Style")
            .Select(style => (string?)style.Attribute(xaml + "Key"))
            .Where(key => key is not null)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var key in new[]
        {
            "RowCard", "RowStatusRail", "RowIcon", "RowIconGlyph", "ConnectionBadge",
            "ConnectionBadgeText", "IconOnlyButton", "DangerButton", "ComponentStatusText",
            "SectionSeparator", "EmptyStateCard"
        })
        {
            Assert.Contains(key, keys);
        }

        foreach (var editorName in new[] { "MountEditorWindow.xaml", "SyncEditorWindow.xaml" })
        {
            var editor = Load(editorName);
            Assert.Contains(editor.Descendants(presentation + "Button"), button =>
                (string?)button.Attribute(xaml + "Name") == "DeleteButton" &&
                (string?)button.Attribute("Style") == "{StaticResource DangerButton}" &&
                !string.IsNullOrWhiteSpace((string?)button.Attribute("AutomationProperties.Name")));
        }
    }

    [Fact]
    public void RowsRemainKeyboardFocusableWithVisibleFocus()
    {
        var document = Load("MainWindow.xaml");
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

        var listItemStyle = Assert.Single(document.Descendants(presentation + "Style"), style =>
            (string?)style.Attribute("TargetType") == "ListBoxItem");
        Assert.Equal("True", SetterValue(listItemStyle, presentation, "Focusable"));
        Assert.Equal("{StaticResource FocusStyle}", SetterValue(listItemStyle, presentation, "FocusVisualStyle"));
        Assert.DoesNotContain(listItemStyle.Descendants(presentation + "Trigger"), trigger =>
            (string?)trigger.Attribute("Property") == "IsKeyboardFocusWithin");

        Assert.Equal("640", (string?)document.Root?.Attribute("MinWidth"));
    }

    [Fact]
    public void UpdateActionsDoNotChainAsyncVoidHandlers()
    {
        var source = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            "MainWindow.xaml.cs")) + File.ReadAllText(Path.Combine(
                AppContext.BaseDirectory, "Fixtures", "MainWindow.Components.cs"));

        Assert.DoesNotContain("InstallApplicationUpdate_Click(sender, e)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("UpdateRclone_Click(sender, e)", source, StringComparison.Ordinal);
        Assert.Contains("private async Task InstallApplicationUpdateAsync()", source, StringComparison.Ordinal);
        Assert.Contains("private async Task UpdateRcloneAsync()", source, StringComparison.Ordinal);
        Assert.Contains("MaximumAutomaticHostRecoveryAttempts = 3", source, StringComparison.Ordinal);
        Assert.Contains("AutomationEvents.LiveRegionChanged", source, StringComparison.Ordinal);
        Assert.Contains("SetProgressText(ApplicationUpdateStatusText", source, StringComparison.Ordinal);
        Assert.Contains("announce: false", source, StringComparison.Ordinal);
        Assert.Contains("private static void SetProgressText", source, StringComparison.Ordinal);
    }

    [Fact]
    public void FixedActionsTrackTheVisibleScrollbarGutterOnEveryScrollablePage()
    {
        XNamespace controls = "clr-namespace:ResoDrive.App.Controls";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

        var mainWindow = Load("MainWindow.xaml");
        var mainOwners = mainWindow.Descendants(controls + "ScrollBarGutter")
            .Select(gutter => (string?)gutter.Attribute("ScrollOwner"))
            .ToArray();
        Assert.Equal(3, mainOwners.Length);
        Assert.Equal("{Binding ElementName=MountRows}", mainOwners[0]);
        Assert.Equal("{Binding ElementName=JobRows}", mainOwners[1]);
        Assert.Equal("{Binding ElementName=SettingsScrollViewer}", mainOwners[2]);

        foreach (var editorName in new[] { "MountEditorWindow.xaml", "SyncEditorWindow.xaml" })
        {
            var editor = Load(editorName);
            Assert.Contains(editor.Descendants(), element =>
                (string?)element.Attribute(xaml + "Name") == "EditorScrollViewer");
            var gutter = Assert.Single(editor.Descendants(controls + "ScrollBarGutter"));
            Assert.Equal("{Binding ElementName=EditorScrollViewer}", (string?)gutter.Attribute("ScrollOwner"));
        }

        var setup = Load("SetupWindow.xaml");
        var setupGutter = Assert.Single(setup.Descendants(controls + "ScrollBarGutter"));
        Assert.Equal("{Binding ElementName=SetupScrollViewer}", (string?)setupGutter.Attribute("ScrollOwner"));
    }

    private static XDocument Load(string name) =>
        XDocument.Load(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    private static string? SetterValue(XElement style, XNamespace presentation, string property) =>
        style.Elements(presentation + "Setter")
            .Single(element => (string?)element.Attribute("Property") == property)
            .Attribute("Value")?.Value;
}
