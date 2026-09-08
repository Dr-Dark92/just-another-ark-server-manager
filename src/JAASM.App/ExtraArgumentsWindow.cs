using Avalonia.Controls;
using Avalonia.Layout;

namespace JAASM.App;

public sealed record LaunchOption(string Argument, string Name, string Description);

public sealed class ExtraArgumentsWindow : Window
{
    private readonly List<(LaunchOption Option, CheckBox Check)> _rows = new();

    public IReadOnlyList<string>? Selection { get; private set; }

    public ExtraArgumentsWindow(IEnumerable<string> selected)
    {
        Title = "ASA Extra Launch Arguments";
        Width = 820;
        Height = 650;
        MinWidth = 680;
        MinHeight = 500;

        var selectedSet = selected.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var body = new StackPanel { Spacing = 10, Margin = new Avalonia.Thickness(18) };

        body.Children.Add(new TextBlock
        {
            Text = "Optional ASA launch arguments",
            FontSize = 22,
            FontWeight = Avalonia.Media.FontWeight.SemiBold
        });
        body.Children.Add(new TextBlock
        {
            Text = "Select only options you actually need. JAASM will append them to the generated server command.",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            Opacity = 0.7
        });

        foreach (var option in Options)
        {
            var check = new CheckBox
            {
                Content = $"{option.Name}   ({option.Argument})",
                IsChecked = selectedSet.Contains(option.Argument)
            };
            var description = new TextBlock
            {
                Text = option.Description,
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                Margin = new Avalonia.Thickness(28, 0, 0, 6),
                Opacity = 0.72
            };
            body.Children.Add(check);
            body.Children.Add(description);
            _rows.Add((option, check));
        }

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 10,
            Margin = new Avalonia.Thickness(0, 12, 0, 0)
        };

        var cancel = new Button { Content = "Cancel" };
        cancel.Click += (_, _) => Close();

        var apply = new Button { Content = "Apply" };
        apply.Click += (_, _) =>
        {
            Selection = _rows.Where(x => x.Check.IsChecked == true)
                .Select(x => x.Option.Argument)
                .ToList();
            Close();
        };

        buttons.Children.Add(cancel);
        buttons.Children.Add(apply);
        body.Children.Add(buttons);

        Content = new ScrollViewer { Content = body };
    }

    public static readonly LaunchOption[] Options =
    {
        new("-NoBattlEye", "Disable BattlEye", "Starts the server without BattlEye anti-cheat. Useful for controlled/private environments, but reduces anti-cheat protection."),
        new("-servergamelog", "Server Game Log", "Enables additional server gameplay logging."),
        new("-servergamelogincludetribelogs", "Include Tribe Logs", "Includes tribe-log activity in the server game log output."),
        new("-ServerRCONOutputTribeLogs", "RCON Tribe Logs", "Forwards tribe-log related output through RCON."),
        new("-NotifyAdminCommandsInChat", "Show Admin Commands", "Notifies players in chat when administrator commands are used."),
        new("-ForceAllowCaveFlyers", "Allow Cave Flyers", "Allows flying creatures in caves where flying is normally restricted."),
        new("-NoWildBabies", "Disable Wild Babies", "Prevents wild baby creature spawning."),
        new("-NoTransferFromDownloading", "Block Download Transfers", "Prevents downloads/transfers from external servers."),
        new("-EnableIdlePlayerKick", "Idle Player Kick", "Enables idle-player kicking behavior."),
        new("-ForceRespawnDinos", "Respawn Wild Dinos", "Forces wild dinosaur respawning during server startup."),
        new("-nosteamclient", "No Steam Client", "Runs without initializing the normal Steam client component; commonly used on dedicated servers."),
        new("-structurememopts", "Structure Memory Optimizations", "Enables server-side structure memory optimizations."),
        new("-ServerPlatform=ALL", "All Supported Platforms", "Advertises/permits the server for all supported platforms when cross-platform configuration permits it.")
    };
}
