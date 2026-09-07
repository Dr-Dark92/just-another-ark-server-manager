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
