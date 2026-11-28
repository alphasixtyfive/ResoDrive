using System.Windows.Controls;
using ResoDrive.Core.Validation;
using WpfComboBox = System.Windows.Controls.ComboBox;

namespace ResoDrive.App.Controls;

public partial class MountOptionsControl : System.Windows.Controls.UserControl
{
    private RcloneMountOptions _options = new([]);
    private bool _loading;
    internal System.Windows.Controls.Control? InvalidInput { get; private set; }
    private static readonly Choice[] Modes =
    [
        new("Read and write", "full"),
        new("Writes only", "writes"), new("Minimal", "minimal"), new("Off", "off")
    ];
    private static readonly Choice[] Sizes =
    [
        new("1 GiB", "1G"), new("2 GiB", "2G"), new("5 GiB", "5G"),
        new("10 GiB", "10G"), new("20 GiB", "20G"), new("Unlimited", "off")
    ];
    private static readonly Choice[] Ages =
    [new("1 hour", "1h"), new("24 hours", "24h"), new("72 hours", "72h"), new("7 days", "7d")];

    public MountOptionsControl()
    {
        InitializeComponent();
        ModeBox.ItemsSource = Modes;
        LoadArguments([], newMount: true);
    }

    public void LoadArguments(IReadOnlyList<string> arguments, bool newMount = false)
    {
        _loading = true;
        _options = new(arguments, newMount);
        ModeBox.SelectedValue = _options.CacheMode;
        SetChoice(SizeBox, Sizes, _options.CacheSize, RcloneMountOptions.CacheSizeOption);
        SetChoice(AgeBox, Ages, _options.CacheAge, RcloneMountOptions.CacheAgeOption);
        ArgumentsBox.Text = RcloneArgumentTextCodec.Format(_options.AdditionalArguments);
        _loading = false;
        UpdateHints();
    }

    public bool TryGetArguments(bool networkMode, out string[] arguments, out string? error)
    {
        InvalidInput = null;
        if (ModeBox.SelectedValue is not string cacheMode || !Modes.Any(mode => mode.Value == cacheMode))
        {
            InvalidInput = ModeBox;
            arguments = [];
            error = "Choose a caching mode.";
            return false;
        }
        if (cacheMode != "off" && (SizeBox.SelectedItem is not Choice || AgeBox.SelectedItem is not Choice))
        {
            InvalidInput = SizeBox.SelectedItem is not Choice ? SizeBox : AgeBox;
            arguments = [];
            error = SizeBox.SelectedItem is not Choice ? "Choose a cache size target." : "Choose when to remove unused cached files.";
            return false;
        }
        var additional = RcloneArgumentTextCodec.Parse(ArgumentsBox.Text);
        if (additional.Any(RcloneMountOptions.IsControlOption))
        {
            InvalidInput = ArgumentsBox;
            arguments = [];
            error = "Use the caching and network-drive controls for cache and network-mode options; remove those options from Additional rclone options.";
            return false;
        }
        arguments = _options.Compose(cacheMode,
            CacheValue(SizeBox, _options.CacheSize),
            CacheValue(AgeBox, _options.CacheAge),
            networkMode, additional);
        var validation = RcloneArgumentPolicy.ValidateMount(arguments);
        // Keep feedback outside the scroll surface concise. The validator still
        // checks every issue; users can correct one actionable error at a time.
        error = validation.IsValid ? null : validation.Issues[0].Message;
        if (!validation.IsValid) InvalidInput = ArgumentsBox;
        return validation.IsValid;
    }

    private static void SetChoice(WpfComboBox box, IEnumerable<Choice> choices, string value, string option)
    {
        var available = choices.ToList();
        var choice = available.FirstOrDefault(item => item.Value == value);
        // Retain a valid saved value without allowing arbitrary new text input.
        if (choice is null && RcloneArgumentPolicy.ValidateMount([option + "=" + value]).IsValid)
        {
            choice = new Choice(value, value);
            available.Add(choice);
        }
        box.ItemsSource = available;
        box.SelectedItem = choice;
    }

    // Missing active choices are rejected above; disabled fields retain their stored value.
    private static string CacheValue(WpfComboBox box, string storedValue) =>
        box.SelectedItem is Choice choice ? choice.Value : storedValue;

    private void Option_Changed(object sender, SelectionChangedEventArgs e) => UpdateHints();

    private void UpdateHints()
    {
        if (_loading || ModeBox is null || SizeBox is null || AgeBox is null) return;
        var mode = ModeBox.SelectedValue as string;
        SizeBox.IsEnabled = AgeBox.IsEnabled = mode != "off";
        ModeBox.ToolTip = mode switch
        {
            "full" => "Caches reads and writes locally.",
            "writes" => "Caches writes. Reads come from the server.",
            "minimal" => "Caches files opened for both reading and writing. Some apps may be incompatible.",
            _ => "Disables disk caching. Some apps may be incompatible."
        };
    }

    private sealed record Choice(string Label, string Value);
}
