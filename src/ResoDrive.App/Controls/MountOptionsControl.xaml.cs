using System.Windows.Controls;
using ResoDrive.Core.Validation;
using WpfComboBox = System.Windows.Controls.ComboBox;

namespace ResoDrive.App.Controls;

public partial class MountOptionsControl : System.Windows.Controls.UserControl
{
    private RcloneMountOptions _options = new([]);
    private bool _loading;
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
        SizeBox.ItemsSource = Sizes;
        AgeBox.ItemsSource = Ages;
        LoadArguments([], newMount: true);
    }

    public void LoadArguments(IReadOnlyList<string> arguments, bool newMount = false)
    {
        _loading = true;
        _options = new(arguments, newMount);
        ModeBox.SelectedValue = _options.CacheMode;
        SetChoice(SizeBox, Sizes, _options.CacheSize);
        SetChoice(AgeBox, Ages, _options.CacheAge);
        ArgumentsBox.Text = RcloneArgumentTextCodec.Format(_options.AdditionalArguments);
        _loading = false;
        UpdateHints();
    }

    public bool TryGetArguments(bool networkMode, out string[] arguments, out string? error)
    {
        if (ModeBox.SelectedValue is not string cacheMode || !Modes.Any(mode => mode.Value == cacheMode))
        {
            arguments = [];
            error = "Choose a caching mode.";
            return false;
        }
        var additional = RcloneArgumentTextCodec.Parse(ArgumentsBox.Text);
        if (additional.Any(RcloneMountOptions.IsControlOption))
        {
            arguments = [];
            error = "Use the caching and network-drive controls for cache and network-mode options; remove those options from Additional rclone options.";
            return false;
        }
        arguments = _options.Compose(cacheMode,
            CacheValue(SizeBox, RcloneMountOptions.CacheSizeOption, _options.CacheSize, cacheMode == "off"),
            CacheValue(AgeBox, RcloneMountOptions.CacheAgeOption, _options.CacheAge, cacheMode == "off"),
            networkMode, additional);
        var validation = RcloneArgumentPolicy.ValidateMount(arguments);
        // Keep feedback outside the scroll surface concise. The validator still
        // checks every issue; users can correct one actionable error at a time.
        error = validation.IsValid ? null : validation.Issues[0].Message;
        return validation.IsValid;
    }

    private static void SetChoice(WpfComboBox box, IEnumerable<Choice> choices, string value)
    {
        var choice = choices.FirstOrDefault(item => item.Value == value);
        box.SelectedItem = choice;
        if (choice is null) box.Text = value;
    }

    private static string ChoiceValue(WpfComboBox box) =>
        box.SelectedItem is Choice choice && box.Text == choice.Label ? choice.Value :
            box.Text.Any(char.IsControl) ? box.Text : box.Text.Trim();

    private static string CacheValue(WpfComboBox box, string option, string storedValue, bool disabled)
    {
        var value = ChoiceValue(box);
        return disabled && !RcloneArgumentPolicy.ValidateMount([option + "=" + value]).IsValid
            ? storedValue : value;
    }

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
