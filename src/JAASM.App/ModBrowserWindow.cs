using System.Diagnostics;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace JAASM.App;

public sealed class ModBrowserWindow : Window
{
    private const string AsaModsHome =
        "https://www.curseforge.com/ark-survival-ascended/search?class=mods";

    private readonly NativeWebView _webView = new();
    private readonly TextBox _addressBox = new();
    private readonly TextBlock _statusText = new();

    public string? SelectedModId { get; private set; }

    public ModBrowserWindow(string? initialSearch = null)
    {
        Title = "JAASM Mod Browser";
        Width = 1280;
        Height = 820;
        MinWidth = 900;
        MinHeight = 600;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var back = new Button { Content = "←", Width = 42 };
        back.Click += (_, _) => _webView.GoBack();

        var forward = new Button { Content = "→", Width = 42 };
        forward.Click += (_, _) => _webView.GoForward();

        var refresh = new Button { Content = "Refresh" };
        refresh.Click += (_, _) => _webView.Refresh();

        var home = new Button { Content = "Mods Home" };
        home.Click += (_, _) => Navigate(BuildSearchUrl(string.Empty));

        _addressBox.HorizontalAlignment = HorizontalAlignment.Stretch;
        _addressBox.PlaceholderText = "CurseForge URL";

        var go = new Button { Content = "Go" };
        go.Click += (_, _) =>
        {
            var raw = _addressBox.Text?.Trim();
            if (Uri.TryCreate(raw, UriKind.Absolute, out var uri))
                Navigate(uri);
        };

        var openExternal = new Button { Content = "Open Externally" };
        openExternal.Click += (_, _) =>
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = _webView.Source.ToString(),
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                _statusText.Text = $"Could not open browser: {ex.Message}";
            }
        };

        var addCurrent = new Button
        {
            Content = "Add Current Mod to Server",
            FontWeight = FontWeight.SemiBold
        };
        addCurrent.Click += async (_, _) => await CaptureCurrentModAsync();

        var top = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,Auto,Auto,Auto,*,Auto,Auto"),
            ColumnSpacing = 8,
            Margin = new Thickness(10, 10, 10, 6)
        };

        AddAt(top, back, 0);
        AddAt(top, forward, 1);
        AddAt(top, refresh, 2);
        AddAt(top, home, 3);
        AddAt(top, _addressBox, 4);
        AddAt(top, go, 5);
        AddAt(top, openExternal, 6);

        var bottom = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            ColumnSpacing = 10,
            Margin = new Thickness(10, 6, 10, 10)
        };

        _statusText.Text =
            "Browse CurseForge normally, open a mod page, then click Add Current Mod to Server.";
        _statusText.VerticalAlignment = VerticalAlignment.Center;
        _statusText.TextWrapping = TextWrapping.Wrap;
        bottom.Children.Add(_statusText);
        Grid.SetColumn(addCurrent, 1);
        bottom.Children.Add(addCurrent);

        var root = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*,Auto")
        };

        root.Children.Add(top);

        Grid.SetRow(_webView, 1);
        root.Children.Add(_webView);

        Grid.SetRow(bottom, 2);
        root.Children.Add(bottom);

        Content = root;

        _webView.NavigationStarted += (_, _) =>
        {
            _statusText.Text = "Loading...";
        };

        _webView.NavigationCompleted += (_, _) =>
        {
            _addressBox.Text = _webView.Source.ToString();
            _statusText.Text =
                "Ready. Open an ARK: Survival Ascended mod page and click Add Current Mod to Server.";
        };

        var searchUrl = BuildSearchUrl(initialSearch ?? string.Empty);
        Navigate(searchUrl);
    }

    private async Task CaptureCurrentModAsync()
    {
        try
        {
            var source = _webView.Source.ToString();

            if (!source.Contains(
                    "/ark-survival-ascended/mods/",
                    StringComparison.OrdinalIgnoreCase))
            {
                _statusText.Text =
                    "This is not an ARK: Survival Ascended mod page. Open a mod first.";
                return;
            }

            var pageText = await _webView.InvokeScript("document.body.innerText");
            var match = Regex.Match(
                pageText ?? string.Empty,
                @"Project\s*ID\s*:?\s*([0-9]{4,10})",
                RegexOptions.IgnoreCase);

            if (!match.Success)
            {
                _statusText.Text =
                    "JAASM could not detect the Project ID on this page. You can still add it manually by ID.";
                return;
            }

            SelectedModId = match.Groups[1].Value;
            Close();
        }
        catch (Exception ex)
        {
            _statusText.Text = $"Could not read current mod page: {ex.Message}";
        }
    }

    private void Navigate(Uri uri)
    {
        _addressBox.Text = uri.ToString();
        _webView.Navigate(uri);
    }

    private static Uri BuildSearchUrl(string search)
    {
        var url = AsaModsHome;

        if (!string.IsNullOrWhiteSpace(search))
            url += "&search=" + Uri.EscapeDataString(search.Trim());

        return new Uri(url);
    }

    private static void AddAt(Grid grid, Control control, int column)
    {
        Grid.SetColumn(control, column);
        grid.Children.Add(control);
    }
}
