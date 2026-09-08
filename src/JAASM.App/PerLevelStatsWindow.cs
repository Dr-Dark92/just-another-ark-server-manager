using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using JAASM.App.Models;

namespace JAASM.App;

public sealed class PerLevelStatsWindow : Window
{
    private static readonly (int Index, string Name, string Description)[] Stats =
    {
        (0, "Health", "Multiplier applied to Health gained from each relevant level-up point."),
        (1, "Stamina", "Multiplier applied to Stamina gained from each relevant level-up point."),
        (2, "Torpidity", "Torpor scaling index. Some player/tamed contexts may ignore this stat."),
        (3, "Oxygen", "Multiplier applied to Oxygen gained from each relevant level-up point."),
        (4, "Food", "Multiplier applied to Food gained from each relevant level-up point."),
        (5, "Water", "Multiplier applied to Water where the target class supports it."),
        (6, "Temperature", "Internal ARK stat index 6. Most practical server profiles leave this at 1.0."),
        (7, "Weight", "Multiplier applied to Weight gained from each relevant level-up point."),
        (8, "Melee Damage", "Multiplier applied to Melee Damage gained from each relevant level-up point."),
        (9, "Movement Speed", "Multiplier applied to Movement Speed where the target class permits scaling."),
        (10, "Fortitude", "Multiplier applied to Fortitude; primarily relevant to players."),
        (11, "Crafting Skill", "Multiplier applied to Crafting Skill; primarily relevant to players.")
    };

    private readonly List<(Dictionary<int, float> Target, int Index, NumericUpDown Box)> _bindings = new();

    public bool Applied { get; private set; }

    public PerLevelStatsWindow(PerLevelStatSettings settings)
    {
        Title = "Per-Level Stats";
        Width = 900;
        Height = 720;
        MinWidth = 760;
        MinHeight = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var tabs = new TabControl();

        tabs.Items.Add(CreateStatTab("Player", settings.Player,
            "Writes PerLevelStatsMultiplier_Player[index] to Game.ini.", isPlayer: true));
        tabs.Items.Add(CreateStatTab("Wild Dino", settings.DinoWild,
            "Writes PerLevelStatsMultiplier_DinoWild[index] to Game.ini."));
        tabs.Items.Add(CreateStatTab("Tamed Level-Up", settings.DinoTamed,
            "Writes PerLevelStatsMultiplier_DinoTamed[index] to Game.ini."));
        tabs.Items.Add(CreateStatTab("Tamed Additive", settings.DinoTamedAdd,
            "Writes PerLevelStatsMultiplier_DinoTamed_Add[index] to Game.ini."));
        tabs.Items.Add(CreateStatTab("Tamed Affinity", settings.DinoTamedAffinity,
            "Writes PerLevelStatsMultiplier_DinoTamed_Affinity[index] to Game.ini."));

        var reset = new Button { Content = "Reset All to 1.0" };
        reset.Click += (_, _) =>
        {
            foreach (var binding in _bindings)
                binding.Box.Value = 1m;
        };

        var cancel = new Button { Content = "Cancel" };
        cancel.Click += (_, _) => Close();

        var apply = new Button { Content = "Apply" };
        apply.Click += (_, _) =>
        {
            foreach (var binding in _bindings)
                binding.Target[binding.Index] = (float)(binding.Box.Value ?? 1m);

            Applied = true;
            Close();
        };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 10
        };
        buttons.Children.Add(reset);
        buttons.Children.Add(cancel);
        buttons.Children.Add(apply);

        var root = new Grid
        {
            RowDefinitions = new RowDefinitions("*,Auto"),
            Margin = new Thickness(16)
        };

        root.Children.Add(tabs);

        var buttonBorder = new Border
        {
            Padding = new Thickness(0, 12, 0, 0),
            Child = buttons
        };
        Grid.SetRow(buttonBorder, 1);
        root.Children.Add(buttonBorder);

        Content = root;
    }

    private TabItem CreateStatTab(string title, Dictionary<int, float> target, string description, bool isPlayer = false)
    {
        var panel = new StackPanel { Spacing = 10, Margin = new Thickness(12) };
        panel.Children.Add(new TextBlock
        {
            Text = description,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            Opacity = 0.7
        });

        foreach (var stat in Stats)
        {
            // Fortitude and Crafting Skill are player-only attributes. Do not expose
            // meaningless dino controls simply because the underlying array has indices.
            if (!isPlayer && stat.Index is 10 or 11)
                continue;

            var box = new NumericUpDown
            {
                Minimum = 0.001m,
                Maximum = 1000m,
                Increment = 0.1m,
                FormatString = "0.000",
                Value = (decimal)(target.TryGetValue(stat.Index, out var value) ? value : 1.0f),
                Width = 160,
                HorizontalAlignment = HorizontalAlignment.Left
            };

            var row = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("320,*"),
                ColumnSpacing = 16
            };

            var info = new StackPanel();
            info.Children.Add(new TextBlock { Text = $"{stat.Name}  [index {stat.Index}]" });
            info.Children.Add(new TextBlock
            {
                Text = stat.Description,
                FontSize = 11,
                Opacity = 0.6,
                TextWrapping = Avalonia.Media.TextWrapping.Wrap
            });

            row.Children.Add(info);
            Grid.SetColumn(box, 1);
            row.Children.Add(box);
            panel.Children.Add(row);

            _bindings.Add((target, stat.Index, box));
        }

        return new TabItem
        {
            Header = title,
            Content = new ScrollViewer { Content = panel }
        };
    }
}
