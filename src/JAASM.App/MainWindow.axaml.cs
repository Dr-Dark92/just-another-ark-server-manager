using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using JAASM.App.Models;
using JAASM.App.Services;

namespace JAASM.App;

public partial class MainWindow : Window
{
    private readonly SteamCmdService _steamCmd = new();
    private readonly SettingsService _settingsService = new();
    private readonly AsaServerService _asaServer;
    private readonly AsaProcessService _asaProcess = new();
    private AppSettings _settings = new();
    private bool _loading;

    public MainWindow()
    {
        InitializeComponent();
        _asaServer = new AsaServerService(_steamCmd);

        PlatformText.Text =
            $"Platform: {Environment.OSVersion.Platform} / {System.Runtime.InteropServices.RuntimeInformation.OSArchitecture}";

        Opened += async (_, _) => await LoadSettingsAsync();
    }

    private async Task LoadSettingsAsync()
    {
        _loading = true;
        _settings = await _settingsService.LoadAsync();

        SteamCmdPathBox.Text = _settings.SteamCmdPath ?? string.Empty;
        SteamCmdInstallDirectoryBox.Text = _settings.SteamCmdInstallDirectory ?? string.Empty;
        AsaInstallDirectoryBox.Text = _settings.AsaServerInstallDirectory ?? string.Empty;
        WebGuiToggle.IsChecked = _settings.WebGui.Enabled;
        LoadProfileControls();
        RefreshProcessState();

        _loading = false;

        if (!string.IsNullOrWhiteSpace(_settings.SteamCmdPath))
        {
            StatusText.Text = File.Exists(_settings.SteamCmdPath)
                ? "Saved SteamCMD configuration loaded."
                : "Saved SteamCMD path no longer exists.";
        }

        if (!string.IsNullOrWhiteSpace(_settings.AsaServerInstallDirectory))
        {
            var validation = _asaServer.ValidateInstallation(_settings.AsaServerInstallDirectory);
            AsaStatusText.Text = validation.Message;
        }
    }

    private async void SelectSteamCmd_Click(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select the existing SteamCMD directory",
            AllowMultiple = false
        });

        if (folders.Count == 0)
            return;

        var path = folders[0].TryGetLocalPath();
        if (path is null)
        {
            StatusText.Text = "JAASM requires a local filesystem directory.";
            return;
        }

        var executable = _steamCmd.ResolveExecutable(path);
        if (executable is null)
        {
            StatusText.Text = $"No {_steamCmd.ExecutableName} was found in the selected directory.";
            return;
        }

        SteamCmdPathBox.Text = executable;
        StatusText.Text = "SteamCMD found. Validating...";

        var validation = await _steamCmd.ValidateDetailedAsync(executable, CreateConsoleProgress());

        if (validation.Success)
        {
            _settings.SteamCmdPath = executable;
            await _settingsService.SaveAsync(_settings);
            StatusText.Text = $"SteamCMD validated and saved: {executable}";
        }
        else
        {
            StatusText.Text = $"SteamCMD exists but validation failed: {validation.Message}";
        }
    }

    private async void BrowseSteamCmdInstallDirectory_Click(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select where JAASM should install SteamCMD",
            AllowMultiple = false
        });

        if (folders.Count == 0)
            return;

        var path = folders[0].TryGetLocalPath();
        if (path is null)
        {
            StatusText.Text = "JAASM requires a local filesystem directory.";
            return;
        }

        SteamCmdInstallDirectoryBox.Text = path;
        _settings.SteamCmdInstallDirectory = path;
        await _settingsService.SaveAsync(_settings);
        StatusText.Text = $"SteamCMD installation directory selected: {path}";
    }

    private async void InstallSteamCmd_Click(object? sender, RoutedEventArgs e)
    {
        var installDirectory = SteamCmdInstallDirectoryBox.Text;

        if (string.IsNullOrWhiteSpace(installDirectory))
        {
            StatusText.Text = "Select the SteamCMD installation directory first.";
            return;
        }

        try
        {
            SetBusy(true);
            DownloadProgress.Value = 0;
            StatusText.Text = $"Preparing SteamCMD installation in {installDirectory}...";

            var progress = new Progress<double>(value => DownloadProgress.Value = value * 100);
            var status = new Progress<string>(message => StatusText.Text = message);

            var executable = await _steamCmd.InstallAsync(
                installDirectory,
                progress,
                status,
                CreateConsoleProgress());

            SteamCmdPathBox.Text = executable;
            _settings.SteamCmdInstallDirectory = installDirectory;
            _settings.SteamCmdPath = executable;

            await _settingsService.SaveAsync(_settings);

            StatusText.Text = $"SteamCMD installed, validated, and saved at {executable}";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"SteamCMD installation failed: {ex.Message}";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void ValidateSteamCmd_Click(object? sender, RoutedEventArgs e)
    {
        var path = SteamCmdPathBox.Text;

        if (string.IsNullOrWhiteSpace(path))
        {
            StatusText.Text = "Select or install SteamCMD first.";
            return;
        }

        SetBusy(true);
        StatusText.Text = "Validating SteamCMD...";

        try
        {
            var validation = await _steamCmd.ValidateDetailedAsync(path, CreateConsoleProgress());

            StatusText.Text = validation.Success
                ? $"SteamCMD validation passed: {path}"
                : $"SteamCMD validation failed: {validation.Message}";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"SteamCMD validation failed: {ex.Message}";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void UpdateSteamCmd_Click(object? sender, RoutedEventArgs e)
    {
        var path = SteamCmdPathBox.Text;

        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            StatusText.Text = "Select or install SteamCMD before checking for updates.";
            return;
        }

        SetBusy(true);
        StatusText.Text = "Checking SteamCMD for updates...";

        try
        {
            var result = await _steamCmd.UpdateAsync(path, CreateConsoleProgress());

            StatusText.Text = result.Success
                ? "SteamCMD update check completed successfully."
                : $"SteamCMD update failed: {result.Message}";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"SteamCMD update failed: {ex.Message}";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void BrowseAsaInstallDirectory_Click(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select where JAASM should install the ASA Dedicated Server",
            AllowMultiple = false
        });

        if (folders.Count == 0)
            return;

        var path = folders[0].TryGetLocalPath();

        if (path is null)
        {
            AsaStatusText.Text = "JAASM requires a local filesystem directory.";
            return;
        }

        AsaInstallDirectoryBox.Text = path;
        _settings.AsaServerInstallDirectory = path;
        await _settingsService.SaveAsync(_settings);

        AsaStatusText.Text = $"ASA installation directory selected: {path}";
    }

    private async void InstallOrUpdateAsa_Click(object? sender, RoutedEventArgs e)
    {
        var steamCmdPath = SteamCmdPathBox.Text;
        var asaDirectory = AsaInstallDirectoryBox.Text;

        if (string.IsNullOrWhiteSpace(steamCmdPath) || !File.Exists(steamCmdPath))
        {
            AsaStatusText.Text = "SteamCMD must be configured before installing ASA.";
            return;
        }

        if (string.IsNullOrWhiteSpace(asaDirectory))
        {
            AsaStatusText.Text = "Select the ASA server installation directory first.";
            return;
        }

        if (!OperatingSystem.IsWindows())
        {
            AsaStatusText.Text =
                "Native ASA Dedicated Server is Windows-targeted. Linux runtime support will be handled separately.";
            return;
        }

        SetBusy(true);
        AsaStatusText.Text = "Installing/updating ASA Dedicated Server through SteamCMD...";

        try
        {
            var result = await _asaServer.InstallOrUpdateAsync(
                steamCmdPath,
                asaDirectory,
                CreateConsoleProgress());

            AsaStatusText.Text = result.Message;

            if (result.Success)
            {
                _settings.AsaServerInstallDirectory = asaDirectory;
                await _settingsService.SaveAsync(_settings);
            }
        }
        catch (Exception ex)
        {
            AsaStatusText.Text = $"ASA install/update failed: {ex.Message}";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void ValidateAsa_Click(object? sender, RoutedEventArgs e)
    {
        var directory = AsaInstallDirectoryBox.Text;
        var result = _asaServer.ValidateInstallation(directory ?? string.Empty);

        AsaStatusText.Text = result.Message;

        if (result.Success)
            AppendConsole($"[ASA READY] {result.ExecutablePath}");
        else
            AppendConsole($"[ASA VALIDATION FAILED] {result.Message}");
    }

    private void LoadProfileControls()
    {
        var p = _settings.AsaProfile;
        ServerNameBox.Text = p.ServerName;
        for (var i = 0; i < MapBox.ItemCount; i++)
        {
            if (MapBox.Items[i] is ComboBoxItem item &&
                string.Equals(item.Tag?.ToString(), p.Map, StringComparison.OrdinalIgnoreCase))
            {
                MapBox.SelectedIndex = i;
                break;
            }
        }
        MaxPlayersBox.Value = p.MaxPlayers;
        GamePortBox.Value = p.GamePort;
        QueryPortBox.Value = p.QueryPort;
        RconPortBox.Value = p.RconPort;
        ServerPasswordBox.Text = p.ServerPassword;
        AdminPasswordBox.Text = p.AdminPassword;
        ExtraArgumentsSummaryText.Text = p.SelectedExtraArguments.Count == 0
            ? "None selected"
            : $"{p.SelectedExtraArguments.Count} selected";
        LaunchPreviewText.Text = BuildLaunchArguments();
    }

    private void ReadProfileControls()
    {
        var p = _settings.AsaProfile;
        p.ServerName = ServerNameBox.Text?.Trim() ?? "JAASM Server";
        p.Map = (MapBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "TheIsland_WP";
        p.MaxPlayers = (int)(MaxPlayersBox.Value ?? 70);
        p.GamePort = (int)(GamePortBox.Value ?? 7777);
        p.QueryPort = (int)(QueryPortBox.Value ?? 27015);
        p.RconPort = (int)(RconPortBox.Value ?? 27020);
        p.ServerPassword = ServerPasswordBox.Text ?? string.Empty;
        p.AdminPassword = AdminPasswordBox.Text ?? string.Empty;
        p.ExtraArguments = string.Join(" ", p.SelectedExtraArguments);
    }

    private string BuildLaunchArguments()
    {
        var p = _settings.AsaProfile;
        var args = $"{p.Map}?SessionName={QuoteUrl(p.ServerName)}?Port={p.GamePort}?QueryPort={p.QueryPort}?RCONPort={p.RconPort}?MaxPlayers={p.MaxPlayers}";

        if (!string.IsNullOrWhiteSpace(p.ServerPassword))
            args += $"?ServerPassword={QuoteUrl(p.ServerPassword)}";
        if (!string.IsNullOrWhiteSpace(p.AdminPassword))
            args += $"?ServerAdminPassword={QuoteUrl(p.AdminPassword)}";

        args += " -server -log";
        if (!string.IsNullOrWhiteSpace(p.ExtraArguments))
            args += " " + p.ExtraArguments;

        return args;
    }

    private static string QuoteUrl(string value) => Uri.EscapeDataString(value);

    private async void ChooseExtraArguments_Click(object? sender, RoutedEventArgs e)
    {
        var dialog = new ExtraArgumentsWindow(_settings.AsaProfile.SelectedExtraArguments);
        await dialog.ShowDialog(this);

        if (dialog.Selection is null)
            return;

        _settings.AsaProfile.SelectedExtraArguments = dialog.Selection.ToList();
        _settings.AsaProfile.ExtraArguments = string.Join(" ", dialog.Selection);
        ExtraArgumentsSummaryText.Text = dialog.Selection.Count == 0
            ? "None selected"
            : $"{dialog.Selection.Count} selected";
        LaunchPreviewText.Text = BuildLaunchArguments();
    }

    private async void SaveProfile_Click(object? sender, RoutedEventArgs e)
    {
        ReadProfileControls();
        await _settingsService.SaveAsync(_settings);
        LaunchPreviewText.Text = BuildLaunchArguments();
        AppendConsole("[PROFILE] ASA server profile saved.");
    }

    private async void StartAsa_Click(object? sender, RoutedEventArgs e)
    {
        ReadProfileControls();
        await _settingsService.SaveAsync(_settings);

        var validation = _asaServer.ValidateInstallation(_settings.AsaServerInstallDirectory ?? string.Empty);
        if (!validation.Success || validation.ExecutablePath is null)
        {
            ProcessStatusText.Text = "Not installed";
            AppendConsole($"[ASA START BLOCKED] {validation.Message}");
            return;
        }

        var args = BuildLaunchArguments();
        LaunchPreviewText.Text = args;
        var state = await _asaProcess.StartAsync(validation.ExecutablePath, args, CreateConsoleProgress());
        RefreshProcessState(state);
    }

    private async void StopAsa_Click(object? sender, RoutedEventArgs e)
    {
        var state = await _asaProcess.StopAsync(CreateConsoleProgress());
        RefreshProcessState(state);
    }

    private async void RestartAsa_Click(object? sender, RoutedEventArgs e)
    {
        ReadProfileControls();
        var validation = _asaServer.ValidateInstallation(_settings.AsaServerInstallDirectory ?? string.Empty);
        if (!validation.Success || validation.ExecutablePath is null)
        {
            AppendConsole($"[ASA RESTART BLOCKED] {validation.Message}");
            return;
        }

        var args = BuildLaunchArguments();
        var state = await _asaProcess.RestartAsync(validation.ExecutablePath, args, CreateConsoleProgress());
        RefreshProcessState(state);
    }

    private async void ForceStopAsa_Click(object? sender, RoutedEventArgs e)
    {
        var state = await _asaProcess.ForceStopAsync(CreateConsoleProgress());
        RefreshProcessState(state);
    }

    private void RefreshProcessState(AsaProcessState? state = null)
    {
        state ??= _asaProcess.GetState();
        ProcessStatusText.Text = state.Running ? "Running" : "Stopped";
        PidText.Text = state.ProcessId?.ToString() ?? "-";
        UptimeText.Text = state.Uptime is null ? "-" : state.Uptime.Value.ToString(@"dd\.hh\:mm\:ss");
    }

    private async void WebGuiToggle_Changed(object? sender, RoutedEventArgs e)
    {
        if (_loading)
            return;

        _settings.WebGui.Enabled = WebGuiToggle.IsChecked == true;
        await _settingsService.SaveAsync(_settings);

        StatusText.Text = _settings.WebGui.Enabled
            ? "Web GUI setting enabled (127.0.0.1:8484)."
            : "Web GUI setting disabled.";
    }

    private IProgress<string> CreateConsoleProgress() =>
        new Progress<string>(AppendConsole);

    private void AppendConsole(string message)
    {
        ConsoleBox.Text += $"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}";
        ConsoleBox.CaretIndex = ConsoleBox.Text?.Length ?? 0;
    }

    private void ClearConsole_Click(object? sender, RoutedEventArgs e) =>
        ConsoleBox.Text = string.Empty;

    private void SetBusy(bool busy)
    {
        DownloadProgress.IsIndeterminate = busy && DownloadProgress.Value <= 0;
    }
}
