using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace JAASM.App;

public sealed class ConfirmDeleteProfileWindow : Window
{
    public bool Confirmed { get; private set; }

    public ConfirmDeleteProfileWindow(string profileName)
    {
        Title = "Delete ARK Server Profile";
        Width = 520;
        Height = 250;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var root = new StackPanel
        {
            Margin = new Thickness(22),
            Spacing = 14
        };

        root.Children.Add(new TextBlock
        {
            Text = "Delete server profile?",
            FontSize = 22,
            FontWeight = FontWeight.SemiBold
        });

        root.Children.Add(new TextBlock
        {
            Text = $"You are about to delete the profile '{profileName}'.",
            TextWrapping = TextWrapping.Wrap
        });

        root.Children.Add(new TextBlock
        {
            Text = "This removes the JAASM profile configuration only. It does not delete the shared ASA installation or server save files.",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.7
        });

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 10,
            Margin = new Thickness(0, 12, 0, 0)
        };

        var cancel = new Button { Content = "Cancel" };
        cancel.Click += (_, _) => Close();

        var delete = new Button { Content = "Delete Profile" };
        delete.Click += (_, _) =>
        {
            Confirmed = true;
            Close();
        };

        buttons.Children.Add(cancel);
        buttons.Children.Add(delete);
        root.Children.Add(buttons);

        Content = root;
    }
}
