using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using System.Diagnostics;
using JAASM.App.Models;
using JAASM.App.Services;

namespace JAASM.App;

public partial class MainWindow : Window
{
    private readonly SteamCmdService _steamCmd = new();
    private readonly SettingsService _settingsService = new();
    private readonly StartupService _startupService = new();
    private readonly BackupService _backupService = new();
    private readonly AsaServerService _asaServer;
    private readonly AsaProcessService _asaProcess = new();
    private readonly AsaConfigService _asaConfig = new();
    private readonly CurseForgeModService _curseForgeMods = new();
    private readonly BrowserModBridgeService _browserModBridge;
    private readonly HttpClient _thumbnailHttp = new();
    private List<AsaModEntry> _browseMods = new();
    private readonly List<EngramCatalogEntry> _engramCatalog = ArkCatalogService.CreateEngrams();
    private readonly List<HarvestResourceCatalogEntry> _harvestCatalog = ArkCatalogService.CreateHarvestResources();

    private AppSettings _settings = new();
    private bool _loading;
    private string? _pendingRestoreArchive;
    private bool _pendingRestoreVerified;
    private AsaServerProfile? ActiveProfile =>
        _settings.AsaProfiles.FirstOrDefault(p => p.Id == _settings.ActiveAsaProfileId);

    public MainWindow()
    {
        InitializeComponent();
        _asaServer = new AsaServerService(_steamCmd);
        _browserModBridge = new BrowserModBridgeService(AddModFromBrowserExtensionAsync);
        LoadCatalogIcons();

        PlatformText.Text =
            $"Platform: {Environment.OSVersion.Platform} / {System.Runtime.InteropServices.RuntimeInformation.OSArchitecture}";

        Opened += async (_, _) =>
        {
            await LoadSettingsAsync();

            try
            {
                await _browserModBridge.StartAsync();
                AppendConsole($"[MOD BRIDGE] Listening on 127.0.0.1:{_browserModBridge.Port}.");
            }
            catch (Exception ex)
            {
                AppendConsole($"[MOD BRIDGE] Could not start browser bridge: {ex.Message}");
            }
        };

        Closed += async (_, _) => await _browserModBridge.DisposeAsync();
    }

    private async Task LoadSettingsAsync()
    {
        _loading = true;
        _settings = await _settingsService.LoadAsync();

        SteamCmdPathBox.Text = _settings.SteamCmdPath ?? string.Empty;
        SteamCmdInstallDirectoryBox.Text = _settings.SteamCmdInstallDirectory ?? string.Empty;
        AsaInstallDirectoryBox.Text = _settings.AsaServerInstallDirectory ?? string.Empty;
        WebGuiToggle.IsChecked = _settings.WebGui.Enabled;
        StartWithOsToggle.IsChecked = _settings.Startup.StartWithOs;
        StartMinimizedToggle.IsChecked = _settings.Startup.StartMinimized;
        RefreshAutoStartProfilesUi();
        BackupDestinationBox.Text = _settings.Backup.DestinationDirectory;
        StartupPlatformText.Text = $"Platform integration: {_startupService.PlatformDescription}";

        var providerKey = !string.IsNullOrWhiteSpace(_settings.ModProvider.CurseForgeApiKey)
            ? _settings.ModProvider.CurseForgeApiKey
            : Environment.GetEnvironmentVariable("JAASM_CURSEFORGE_API_KEY") ?? string.Empty;

        _curseForgeMods.ConfigureApiKey(providerKey);

        ModBrowseStatusText.Text = _curseForgeMods.IsConfigured
            ? "Mod catalogue connected through CurseForge API."
            : "Public mod catalogue fallback active.";

        EnsureProfiles();
        RefreshProfileTabs();

        if (ActiveProfile is not null)
        {
            LoadProfileControls();
            RefreshProcessState();
            BackupProfileText.Text = $"Active profile: {ActiveProfile.ServerName}";
        }
        else
        {
            ShowNoProfileState();
        }

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

        if (ActiveProfile is not null)
            await ResolveMissingModMetadataAsync(ActiveProfile);

        if (_settings.Startup.StartMinimized)
            WindowState = WindowState.Minimized;

        if (_settings.Startup.AutoStartProfileIds.Count == 0 && _settings.Startup.AutoStartActiveServer && ActiveProfile is not null)
            _settings.Startup.AutoStartProfileIds.Add(ActiveProfile.Id);
        if (_settings.Startup.AutoStartProfileIds.Count > 0)
            await AutoStartProfilesAsync();
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

        // The selected folder is the JAASM server root. Keep SteamCMD and the
        // game server together underneath it instead of creating sibling folders.
        var steamDirectory = Path.Combine(path, "Steam");
        var gameServerDirectory = Path.Combine(path, "Games Server");

        SteamCmdInstallDirectoryBox.Text = steamDirectory;
        AsaInstallDirectoryBox.Text = gameServerDirectory;

        _settings.SteamCmdInstallDirectory = steamDirectory;
        _settings.AsaServerInstallDirectory = gameServerDirectory;
        await _settingsService.SaveAsync(_settings);

        StatusText.Text = $"Server root selected: {path}";
        AsaStatusText.Text = $"Default game server directory: {gameServerDirectory}";
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
            AppendConsole("[ASA] Linux host detected. JAASM will download/update the Windows ASA dedicated-server files through SteamCMD.");
            AppendConsole("[ASA] Runtime launch remains disabled until Linux compatibility/runtime support is implemented.");
        }

        SetBusy(true);
        AsaDownloadProgress.IsVisible = true;
        AsaDownloadProgress.IsIndeterminate = true;
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
            AsaDownloadProgress.IsIndeterminate = false;
            AsaDownloadProgress.IsVisible = false;
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

    private void EnsureProfiles()
    {
        // Fresh installations intentionally start with zero server profiles.
        // Existing multi-profile settings are preserved.
        if (_settings.AsaProfiles.Count == 0)
        {
            _settings.ActiveAsaProfileId = null;
            return;
        }

        if (string.IsNullOrWhiteSpace(_settings.ActiveAsaProfileId) ||
            !_settings.AsaProfiles.Any(p => p.Id == _settings.ActiveAsaProfileId))
        {
            _settings.ActiveAsaProfileId = _settings.AsaProfiles[0].Id;
        }
    }

    private void RefreshProfileTabs()
    {
        _loading = true;

        ProfileTabStrip.Children.Clear();

        foreach (var profile in _settings.AsaProfiles)
        {
            var button = new Button
            {
                Content = profile.ServerName,
                Tag = profile.Id,
                Margin = new Avalonia.Thickness(0, 0, 4, 0),
                Padding = new Avalonia.Thickness(14, 8)
            };

            if (ActiveProfile?.Id == profile.Id)
                button.FontWeight = Avalonia.Media.FontWeight.SemiBold;

            button.Click += ProfileTabButton_Click;
            ProfileTabStrip.Children.Add(button);
        }

        var addButton = new Button
        {
            Content = "Add New Server +",
            Tag = "__add__",
            Margin = new Avalonia.Thickness(4, 0, 0, 0),
            Padding = new Avalonia.Thickness(14, 8)
        };
        addButton.Click += CreateProfile_Click;
        ProfileTabStrip.Children.Add(addButton);

        _loading = false;

        // Keep every profile-dependent UI synchronized from this single refresh path.
        // This includes create/delete/restore/import flows that already call RefreshProfileTabs().
        RefreshAutoStartProfilesUi();

        NoProfilePanel.IsVisible = ActiveProfile is null;
        ProfileEditorPanel.IsVisible = ActiveProfile is not null;
    }

    private async void ProfileTabButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_loading || sender is not Button button)
            return;

        var id = button.Tag?.ToString();
        var selected = _settings.AsaProfiles.FirstOrDefault(p => p.Id == id);
        if (selected is null)
            return;

        if (ActiveProfile is not null && ActiveProfile.Id != selected.Id)
            ReadProfileControls();

        _settings.ActiveAsaProfileId = selected.Id;
        await _settingsService.SaveAsync(_settings);

        RefreshProfileTabs();
        LoadProfileControls();
        RefreshProcessState();
        NoProfilePanel.IsVisible = false;
        ProfileEditorPanel.IsVisible = true;
    }

    private async void CreateProfile_Click(object? sender, RoutedEventArgs e) =>
        await CreateProfileAsync();

    private async Task CreateProfileAsync()
    {
        var index = _settings.AsaProfiles.Count + 1;
        var profile = new AsaServerProfile
        {
            ServerName = $"ARK Server {index}",
            GamePort = 7777 + ((_settings.AsaProfiles.Count) * 10),
            QueryPort = 27015 + _settings.AsaProfiles.Count,
            RconPort = 27020 + _settings.AsaProfiles.Count
        };

        _settings.AsaProfiles.Add(profile);
        _settings.ActiveAsaProfileId = profile.Id;

        await _settingsService.SaveAsync(_settings);

        RefreshProfileTabs();
        LoadProfileControls();
        RefreshProcessState();

        AppendConsole($"[PROFILE] Created {profile.ServerName} ({profile.Id}).");
    }

    private async void DeleteProfile_Click(object? sender, RoutedEventArgs e)
    {
        var profile = ActiveProfile;
        if (profile is null)
            return;

        if (_asaProcess.GetState(profile.Id).Running)
        {
            AppendConsole("[PROFILE] Stop the server before deleting its profile.");
            return;
        }

        var confirm = new ConfirmDeleteProfileWindow(profile.ServerName);
        await confirm.ShowDialog(this);

        if (!confirm.Confirmed)
        {
            AppendConsole($"[PROFILE] Deletion cancelled for {profile.ServerName}.");
            return;
        }

        _settings.AsaProfiles.Remove(profile);
        _settings.ActiveAsaProfileId =
            _settings.AsaProfiles.Count > 0 ? _settings.AsaProfiles[0].Id : null;

        await _settingsService.SaveAsync(_settings);

        RefreshProfileTabs();

        if (ActiveProfile is not null)
        {
            LoadProfileControls();
            RefreshProcessState();
        }
        else
        {
            ShowNoProfileState();
        }

        AppendConsole($"[PROFILE] Deleted {profile.ServerName}.");
    }

    private void ShowNoProfileState()
    {
        NoProfilePanel.IsVisible = true;
        ProfileEditorPanel.IsVisible = false;
        ProfileOverviewPanel.IsVisible = false;
        ProcessStatusText.Text = "Stopped";
        PidText.Text = "-";
        UptimeText.Text = "-";
    }

    private void LoadProfileControls()
    {
        var p = ActiveProfile;
        if (p is null)
            return;
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
        AllowPcBox.IsChecked = p.AllowPc;
        AllowXboxBox.IsChecked = p.AllowXbox;
        AllowPs5Box.IsChecked = p.AllowPs5;
        ServerPasswordBox.Text = p.ServerPassword;
        AdminPasswordBox.Text = p.AdminPassword;
        ManualArgumentsBox.Text = p.ExtraArguments;
        ExtraArgumentsSummaryText.Text = p.SelectedExtraArguments.Count == 0
            ? "None selected"
            : $"{p.SelectedExtraArguments.Count} selected";

        ProfileOverviewPanel.IsVisible = true;
        ActiveProfileNameText.Text = p.ServerName;
        ActiveProfileMapText.Text = (MapBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? p.Map;
        ActiveProfilePortsText.Text = $"{p.GamePort} / {p.QueryPort} / {p.RconPort}";

        LoadCustomizationControls(p.Customization);
        LoadPerLevelStatsControls(p.PerLevelStats);
        RefreshPerLevelStatsSummary();
        SyncCatalogSelectionsFromProfile();
        RefreshEngramCatalogUi();
        RefreshHarvestCatalogUi();
        RefreshModsUi();
        UpdateLaunchCommandDisplay();
    }

    private void ReadProfileControls()
    {
        var p = ActiveProfile;
        if (p is null)
            return;
        p.ServerName = ServerNameBox.Text?.Trim() ?? "JAASM Server";
        p.Map = (MapBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "TheIsland_WP";
        p.MaxPlayers = (int)(MaxPlayersBox.Value ?? 70);
        p.GamePort = (int)(GamePortBox.Value ?? 7777);
        p.QueryPort = (int)(QueryPortBox.Value ?? 27015);
        p.RconPort = (int)(RconPortBox.Value ?? 27020);
        p.AllowPc = AllowPcBox.IsChecked == true;
        p.AllowXbox = AllowXboxBox.IsChecked == true;
        p.AllowPs5 = AllowPs5Box.IsChecked == true;
        p.ServerPassword = ServerPasswordBox.Text ?? string.Empty;
        p.AdminPassword = AdminPasswordBox.Text ?? string.Empty;
        p.ExtraArguments = ManualArgumentsBox.Text?.Trim() ?? string.Empty;
        if (p.UseManualLaunchCommand)
            p.ManualLaunchCommand = LaunchCommandBox.Text?.Trim() ?? string.Empty;
        ReadCustomizationControls(p.Customization);
        ReadPerLevelStatsControls(p.PerLevelStats);
    }

    private string BuildLaunchArguments()
    {
        var p = ActiveProfile;
        if (p is null)
            return string.Empty;
        var args = $"{p.Map}?listen?SessionName={QuoteUrl(p.ServerName)}";

        if (!string.IsNullOrWhiteSpace(p.ServerPassword))
            args += $"?ServerPassword={QuoteUrl(p.ServerPassword)}";
        var saveName = $"JAASM_{p.Id[..Math.Min(8, p.Id.Length)]}";
        args += $"?AltSaveDirectoryName={saveName}";

        if (!string.IsNullOrWhiteSpace(p.AdminPassword))
            args += $"?ServerAdminPassword={QuoteUrl(p.AdminPassword)}";

        args += $" -port={p.GamePort} -QueryPort={p.QueryPort} -RCONPort={p.RconPort} -WinLiveMaxPlayers={p.MaxPlayers}";
        args += " -ServerPlatform=" + BuildServerPlatformArgument(p);
        args += $" -log -AltLogDirectoryName=\"{saveName}/Logs\"";
        var enabledMods = p.Mods
            .Where(m => m.Enabled)
            .OrderBy(m => m.LoadOrder)
            .Select(m => m.ModId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToList();

        if (enabledMods.Count > 0)
            args += " -mods=" + string.Join(",", enabledMods);

        if (!string.IsNullOrWhiteSpace(p.ExtraArguments))
            args += " " + p.ExtraArguments;

        return args;
    }

    private static string BuildServerPlatformArgument(AsaServerProfile profile)
    {
        if (profile.AllowPc && profile.AllowXbox && profile.AllowPs5)
            return "ALL";

        var platforms = new List<string>();
        if (profile.AllowPc)
            platforms.Add("PC");
        if (profile.AllowXbox)
            platforms.Add("XSX");
        if (profile.AllowPs5)
            platforms.Add("PS5");

        // Never emit an empty platform list. A profile with every box cleared
        // falls back to PC so the server remains reachable for correction.
        return platforms.Count == 0 ? "PC" : string.Join("+", platforms);
    }

    private string GetEffectiveLaunchArguments(AsaServerProfile profile)
    {
        if (profile.UseManualLaunchCommand && !string.IsNullOrWhiteSpace(profile.ManualLaunchCommand))
            return profile.ManualLaunchCommand.Trim();

        return BuildLaunchArguments();
    }

    private void UpdateLaunchCommandDisplay()
    {
        var profile = ActiveProfile;
        if (profile is null)
        {
            LaunchCommandBox.Text = string.Empty;
            return;
        }

        LaunchCommandBox.Text = GetEffectiveLaunchArguments(profile);
        LaunchCommandBox.IsReadOnly = !profile.UseManualLaunchCommand;
        LaunchCommandWarningText.IsVisible = profile.UseManualLaunchCommand;
        ResetLaunchCommandButton.IsVisible = profile.UseManualLaunchCommand;
        EditLaunchCommandButton.IsVisible = !profile.UseManualLaunchCommand;
    }

    private async void EditLaunchCommand_Click(object? sender, RoutedEventArgs e)
    {
        var profile = ActiveProfile;
        if (profile is null)
            return;

        profile.ManualLaunchCommand = BuildLaunchArguments();
        profile.UseManualLaunchCommand = true;
        LaunchCommandBox.Text = profile.ManualLaunchCommand;
        LaunchCommandBox.IsReadOnly = false;
        LaunchCommandWarningText.IsVisible = true;
        ResetLaunchCommandButton.IsVisible = true;
        EditLaunchCommandButton.IsVisible = false;
        await _settingsService.SaveAsync(_settings);
        AppendConsole("[PROFILE] Manual launch command override enabled.");
    }

    private async void ResetLaunchCommand_Click(object? sender, RoutedEventArgs e)
    {
        var profile = ActiveProfile;
        if (profile is null)
            return;

        profile.UseManualLaunchCommand = false;
        profile.ManualLaunchCommand = string.Empty;
        await _settingsService.SaveAsync(_settings);
        UpdateLaunchCommandDisplay();
        AppendConsole("[PROFILE] Manual launch command override removed; generated command restored.");
    }

    private async void RestoreProfileDefaults_Click(object? sender, RoutedEventArgs e)
    {
        var profile = ActiveProfile;
        if (profile is null)
            return;

        var defaults = new AsaServerProfile();
        profile.ServerName = defaults.ServerName;
        profile.Map = defaults.Map;
        profile.MaxPlayers = defaults.MaxPlayers;
        profile.GamePort = defaults.GamePort;
        profile.QueryPort = defaults.QueryPort;
        profile.RconPort = defaults.RconPort;
        profile.ServerPassword = defaults.ServerPassword;
        profile.AdminPassword = defaults.AdminPassword;
        profile.ExtraArguments = defaults.ExtraArguments;
        profile.SelectedExtraArguments = new();
        profile.AllowPc = defaults.AllowPc;
        profile.AllowXbox = defaults.AllowXbox;
        profile.AllowPs5 = defaults.AllowPs5;
        profile.UseManualLaunchCommand = false;
        profile.ManualLaunchCommand = string.Empty;
        profile.Customization = new AsaCustomizationSettings();
        profile.PerLevelStats = new PerLevelStatSettings();
        profile.EngramOverrides = new();
        profile.HarvestResourceMultipliers = new();

        await _settingsService.SaveAsync(_settings);
        LoadProfileControls();
        RefreshProfileTabs();
        AppendConsole($"[PROFILE] {profile.ServerName} configuration restored to JAASM defaults. Mods were preserved.");
    }

    private static string QuoteUrl(string value) => Uri.EscapeDataString(value);

    private async void ChooseExtraArguments_Click(object? sender, RoutedEventArgs e)
    {
        var profile = ActiveProfile;
        if (profile is null)
            return;

        var dialog = new ExtraArgumentsWindow(profile.SelectedExtraArguments);
        await dialog.ShowDialog(this);

        if (dialog.Selection is null)
            return;

        profile.SelectedExtraArguments = dialog.Selection.ToList();
        profile.ExtraArguments = string.Join(" ", dialog.Selection);
        ManualArgumentsBox.Text = profile.ExtraArguments;
        ExtraArgumentsSummaryText.Text = dialog.Selection.Count == 0
            ? "None selected"
            : $"{dialog.Selection.Count} selected";
        UpdateLaunchCommandDisplay();
    }

    private async void SaveProfile_Click(object? sender, RoutedEventArgs e)
    {
        var profile = ActiveProfile;
        if (profile is null)
            return;

        ReadProfileControls();
        await _settingsService.SaveAsync(_settings);
        UpdateLaunchCommandDisplay();
        RefreshProfileTabs();

        if (!string.IsNullOrWhiteSpace(_settings.AsaServerInstallDirectory))
        {
            try
            {
                var result = await _asaConfig.WriteGameUserSettingsAsync(
                    _settings.AsaServerInstallDirectory,
                    profile);
                AppendConsole($"[CONFIG] Generated {result.LivePath}");
                AppendConsole($"[CONFIG] Generated {result.LiveGameIniPath}");
                AppendConsole($"[CONFIG SNAPSHOT] {result.ProfileSnapshotPath}");
                AppendConsole($"[CONFIG SNAPSHOT] {result.ProfileGameIniSnapshotPath}");
            }
            catch (Exception ex)
            {
                AppendConsole($"[CONFIG ERROR] {ex.Message}");
            }
        }

        AppendConsole($"[PROFILE] {profile.ServerName} saved.");
    }

    private async void StartAsa_Click(object? sender, RoutedEventArgs e)
    {
        var profile = ActiveProfile;
        if (profile is null)
            return;

        ReadProfileControls();
        await _settingsService.SaveAsync(_settings);

        var validation = _asaServer.ValidateInstallation(_settings.AsaServerInstallDirectory ?? string.Empty);
        if (!validation.Success || validation.ExecutablePath is null)
        {
            ProcessStatusText.Text = "Not installed";
            AppendConsole($"[ASA START BLOCKED] {validation.Message}");
            return;
        }

        try
        {
            var configResult = await _asaConfig.WriteGameUserSettingsAsync(
                _settings.AsaServerInstallDirectory ?? string.Empty,
                profile);
            AppendConsole($"[CONFIG] Activated profile configs: {configResult.LivePath}");
            AppendConsole($"[CONFIG] Activated Game.ini: {configResult.LiveGameIniPath}");
        }
        catch (Exception ex)
        {
            AppendConsole($"[ASA START BLOCKED] Could not generate configuration: {ex.Message}");
            return;
        }

        var args = GetEffectiveLaunchArguments(profile);
        UpdateLaunchCommandDisplay();
        var state = await _asaProcess.StartAsync(profile.Id, validation.ExecutablePath, args, CreateConsoleProgress());
        RefreshProcessState(state);
    }

    private async void StopAsa_Click(object? sender, RoutedEventArgs e)
    {
        var profile = ActiveProfile;
        if (profile is null)
            return;

        var state = await _asaProcess.StopAsync(profile.Id, CreateConsoleProgress());
        RefreshProcessState(state);
    }

    private async void RestartAsa_Click(object? sender, RoutedEventArgs e)
    {
        var profile = ActiveProfile;
        if (profile is null)
            return;

        ReadProfileControls();
        var validation = _asaServer.ValidateInstallation(_settings.AsaServerInstallDirectory ?? string.Empty);
        if (!validation.Success || validation.ExecutablePath is null)
        {
            AppendConsole($"[ASA RESTART BLOCKED] {validation.Message}");
            return;
        }

        try
        {
            var configResult = await _asaConfig.WriteGameUserSettingsAsync(
                _settings.AsaServerInstallDirectory ?? string.Empty,
                profile);
            AppendConsole($"[CONFIG] Activated profile configs: {configResult.LivePath}");
            AppendConsole($"[CONFIG] Activated Game.ini: {configResult.LiveGameIniPath}");
        }
        catch (Exception ex)
        {
            AppendConsole($"[ASA RESTART BLOCKED] Could not generate configuration: {ex.Message}");
            return;
        }

        var args = GetEffectiveLaunchArguments(profile);
        UpdateLaunchCommandDisplay();
        var state = await _asaProcess.RestartAsync(profile.Id, validation.ExecutablePath, args, CreateConsoleProgress());
        RefreshProcessState(state);
    }

    private async void ForceStopAsa_Click(object? sender, RoutedEventArgs e)
    {
        var profile = ActiveProfile;
        if (profile is null)
            return;

        var state = await _asaProcess.ForceStopAsync(profile.Id, CreateConsoleProgress());
        RefreshProcessState(state);
    }

    private void RefreshProcessState(AsaProcessState? state = null)
    {
        var profile = ActiveProfile;
        if (profile is null)
        {
            ShowNoProfileState();
            return;
        }

        state ??= _asaProcess.GetState(profile.Id);
        ProcessStatusText.Text = state.Running ? "Running" : "Stopped";
        ActiveProfileStateText.Text = state.Running ? "Running" : "Stopped";
        PidText.Text = state.ProcessId?.ToString() ?? "-";
        UptimeText.Text = state.Uptime is null ? "-" : state.Uptime.Value.ToString(@"dd\.hh\:mm\:ss");
    }

    private void LoadCustomizationControls(AsaCustomizationSettings s)
    {
        PveModeBox.SelectedIndex = s.PvE ? 0 : 1;
        AllowThirdPersonToggle.IsChecked = s.AllowThirdPerson;
        ShowMapPlayerLocationToggle.IsChecked = s.ShowMapPlayerLocation;
        ServerCrosshairToggle.IsChecked = s.ServerCrosshair;
        AllowHitMarkersToggle.IsChecked = s.AllowHitMarkers;
        DisableStructureDecayPveToggle.IsChecked = s.DisableStructureDecayPvE;
        DisableDinoDecayPveToggle.IsChecked = s.DisableDinoDecayPvE;
        PreventOfflinePvpToggle.IsChecked = s.PreventOfflinePvP;
        DifficultyOffsetBox.Value = (decimal)s.DifficultyOffset;
        OverrideOfficialDifficultyBox.Value = (decimal)s.OverrideOfficialDifficulty;

        XpMultiplierBox.Value = (decimal)s.XpMultiplier;
        TamingSpeedBox.Value = (decimal)s.TamingSpeedMultiplier;
        HarvestAmountBox.Value = (decimal)s.HarvestAmountMultiplier;
        HarvestHealthBox.Value = (decimal)s.HarvestHealthMultiplier;
        PlayerFoodDrainBox.Value = (decimal)s.PlayerCharacterFoodDrainMultiplier;
        PlayerWaterDrainBox.Value = (decimal)s.PlayerCharacterWaterDrainMultiplier;
        DinoFoodDrainBox.Value = (decimal)s.DinoCharacterFoodDrainMultiplier;

        DayCycleSpeedBox.Value = (decimal)s.DayCycleSpeedScale;
        DayTimeSpeedBox.Value = (decimal)s.DayTimeSpeedScale;
        NightTimeSpeedBox.Value = (decimal)s.NightTimeSpeedScale;
        DinoCountBox.Value = (decimal)s.DinoCountMultiplier;
        ResourceRespawnBox.Value = (decimal)s.ResourcesRespawnPeriodMultiplier;
        SpoilingTimeBox.Value = (decimal)s.GlobalSpoilingTimeMultiplier;
        ItemDecompositionBox.Value = (decimal)s.GlobalItemDecompositionTimeMultiplier;
        CorpseDecompositionBox.Value = (decimal)s.GlobalCorpseDecompositionTimeMultiplier;

        MatingIntervalBox.Value = (decimal)s.MatingIntervalMultiplier;
        EggHatchSpeedBox.Value = (decimal)s.EggHatchSpeedMultiplier;
        BabyMatureSpeedBox.Value = (decimal)s.BabyMatureSpeedMultiplier;
        BabyCuddleIntervalBox.Value = (decimal)s.BabyCuddleIntervalMultiplier;
        BabyImprintAmountBox.Value = (decimal)s.BabyImprintAmountMultiplier;
        BabyFoodConsumptionBox.Value = (decimal)s.BabyFoodConsumptionSpeedMultiplier;

        StructureResistanceBox.Value = (decimal)s.StructureResistanceMultiplier;
        StructureDamageBox.Value = (decimal)s.StructureDamageMultiplier;
        PveStructureDecayPeriodBox.Value = (decimal)s.PvEStructureDecayPeriodMultiplier;
        PveDinoDecayPeriodBox.Value = (decimal)s.PvEDinoDecayPeriodMultiplier;
        AllowCaveBuildingPveToggle.IsChecked = s.AllowCaveBuildingPvE;
        ExtraStructurePreventionToggle.IsChecked = s.EnableExtraStructurePreventionVolumes;
        PvpStructureDecayToggle.IsChecked = s.PvPStructureDecay;

        PlayerDamageBox.Value = (decimal)s.PlayerDamageMultiplier;
        PlayerResistanceBox.Value = (decimal)s.PlayerResistanceMultiplier;
        DinoDamageBox.Value = (decimal)s.DinoDamageMultiplier;
        DinoResistanceBox.Value = (decimal)s.DinoResistanceMultiplier;
        PlayerStaminaDrainBox.Value = (decimal)s.PlayerStaminaDrainMultiplier;
        DinoStaminaDrainBox.Value = (decimal)s.DinoStaminaDrainMultiplier;
        PlayerHealthRecoveryBox.Value = (decimal)s.PlayerHealthRecoveryMultiplier;
        DinoHealthRecoveryBox.Value = (decimal)s.DinoHealthRecoveryMultiplier;
    }

    private void ReadCustomizationControls(AsaCustomizationSettings s)
    {
        s.PvE = PveModeBox.SelectedIndex != 1;
        s.AllowThirdPerson = AllowThirdPersonToggle.IsChecked == true;
        s.ShowMapPlayerLocation = ShowMapPlayerLocationToggle.IsChecked == true;
        s.ServerCrosshair = ServerCrosshairToggle.IsChecked == true;
        s.AllowHitMarkers = AllowHitMarkersToggle.IsChecked == true;
        s.DisableStructureDecayPvE = DisableStructureDecayPveToggle.IsChecked == true;
        s.DisableDinoDecayPvE = DisableDinoDecayPveToggle.IsChecked == true;
        s.PreventOfflinePvP = PreventOfflinePvpToggle.IsChecked == true;
        s.DifficultyOffset = (float)(DifficultyOffsetBox.Value ?? 1m);
        s.OverrideOfficialDifficulty = (float)(OverrideOfficialDifficultyBox.Value ?? 5m);

        s.XpMultiplier = (float)(XpMultiplierBox.Value ?? 1m);
        s.TamingSpeedMultiplier = (float)(TamingSpeedBox.Value ?? 1m);
        s.HarvestAmountMultiplier = (float)(HarvestAmountBox.Value ?? 1m);
        s.HarvestHealthMultiplier = (float)(HarvestHealthBox.Value ?? 1m);
        s.PlayerCharacterFoodDrainMultiplier = (float)(PlayerFoodDrainBox.Value ?? 1m);
        s.PlayerCharacterWaterDrainMultiplier = (float)(PlayerWaterDrainBox.Value ?? 1m);
        s.DinoCharacterFoodDrainMultiplier = (float)(DinoFoodDrainBox.Value ?? 1m);

        s.DayCycleSpeedScale = (float)(DayCycleSpeedBox.Value ?? 1m);
        s.DayTimeSpeedScale = (float)(DayTimeSpeedBox.Value ?? 1m);
        s.NightTimeSpeedScale = (float)(NightTimeSpeedBox.Value ?? 1m);
        s.DinoCountMultiplier = (float)(DinoCountBox.Value ?? 1m);
        s.ResourcesRespawnPeriodMultiplier = (float)(ResourceRespawnBox.Value ?? 1m);
        s.GlobalSpoilingTimeMultiplier = (float)(SpoilingTimeBox.Value ?? 1m);
        s.GlobalItemDecompositionTimeMultiplier = (float)(ItemDecompositionBox.Value ?? 1m);
        s.GlobalCorpseDecompositionTimeMultiplier = (float)(CorpseDecompositionBox.Value ?? 1m);

        s.MatingIntervalMultiplier = (float)(MatingIntervalBox.Value ?? 1m);
        s.EggHatchSpeedMultiplier = (float)(EggHatchSpeedBox.Value ?? 1m);
        s.BabyMatureSpeedMultiplier = (float)(BabyMatureSpeedBox.Value ?? 1m);
        s.BabyCuddleIntervalMultiplier = (float)(BabyCuddleIntervalBox.Value ?? 1m);
        s.BabyImprintAmountMultiplier = (float)(BabyImprintAmountBox.Value ?? 1m);
        s.BabyFoodConsumptionSpeedMultiplier = (float)(BabyFoodConsumptionBox.Value ?? 1m);

        s.StructureResistanceMultiplier = (float)(StructureResistanceBox.Value ?? 1m);
        s.StructureDamageMultiplier = (float)(StructureDamageBox.Value ?? 1m);
        s.PvEStructureDecayPeriodMultiplier = (float)(PveStructureDecayPeriodBox.Value ?? 1m);
        s.PvEDinoDecayPeriodMultiplier = (float)(PveDinoDecayPeriodBox.Value ?? 1m);
        s.AllowCaveBuildingPvE = AllowCaveBuildingPveToggle.IsChecked == true;
        s.EnableExtraStructurePreventionVolumes = ExtraStructurePreventionToggle.IsChecked == true;
        s.PvPStructureDecay = PvpStructureDecayToggle.IsChecked == true;

        s.PlayerDamageMultiplier = (float)(PlayerDamageBox.Value ?? 1m);
        s.PlayerResistanceMultiplier = (float)(PlayerResistanceBox.Value ?? 1m);
        s.DinoDamageMultiplier = (float)(DinoDamageBox.Value ?? 1m);
        s.DinoResistanceMultiplier = (float)(DinoResistanceBox.Value ?? 1m);
        s.PlayerStaminaDrainMultiplier = (float)(PlayerStaminaDrainBox.Value ?? 1m);
        s.DinoStaminaDrainMultiplier = (float)(DinoStaminaDrainBox.Value ?? 1m);
        s.PlayerHealthRecoveryMultiplier = (float)(PlayerHealthRecoveryBox.Value ?? 1m);
        s.DinoHealthRecoveryMultiplier = (float)(DinoHealthRecoveryBox.Value ?? 1m);
    }

    private void LoadPerLevelStatsControls(PerLevelStatSettings s)
    {
        LoadStatFamily("Player", s.Player, 12);
        LoadStatFamily("DinoWild", s.DinoWild, 10);
        LoadStatFamily("DinoTamed", s.DinoTamed, 10);
        LoadStatFamily("DinoTamedAdd", s.DinoTamedAdd, 10);
        LoadStatFamily("DinoTamedAffinity", s.DinoTamedAffinity, 10);
    }

    private void ReadPerLevelStatsControls(PerLevelStatSettings s)
    {
        ReadStatFamily("Player", s.Player, 12);
        ReadStatFamily("DinoWild", s.DinoWild, 10);
        ReadStatFamily("DinoTamed", s.DinoTamed, 10);
        ReadStatFamily("DinoTamedAdd", s.DinoTamedAdd, 10);
        ReadStatFamily("DinoTamedAffinity", s.DinoTamedAffinity, 10);
        RefreshPerLevelStatsSummary();
    }

    private void LoadStatFamily(string prefix, Dictionary<int, float> target, int count)
    {
        for (var i = 0; i < count; i++)
        {
            if (this.FindControl<NumericUpDown>($"{prefix}Stat{i}Box") is { } box)
                box.Value = (decimal)(target.TryGetValue(i, out var value) ? value : 1.0f);
        }
    }

    private void ReadStatFamily(string prefix, Dictionary<int, float> target, int count)
    {
        for (var i = 0; i < count; i++)
        {
            if (this.FindControl<NumericUpDown>($"{prefix}Stat{i}Box") is { } box)
                target[i] = (float)(box.Value ?? 1m);
        }
    }

    private void ResetPerLevelStats_Click(object? sender, RoutedEventArgs e)
    {
        var profile = ActiveProfile;
        if (profile is null)
            return;

        foreach (var family in new[]
        {
            profile.PerLevelStats.Player,
            profile.PerLevelStats.DinoWild,
            profile.PerLevelStats.DinoTamed,
            profile.PerLevelStats.DinoTamedAdd,
            profile.PerLevelStats.DinoTamedAffinity
        })
        {
            foreach (var key in family.Keys.ToList())
                family[key] = 1.0f;
        }

        LoadPerLevelStatsControls(profile.PerLevelStats);
        RefreshPerLevelStatsSummary();
        AppendConsole($"[PER-LEVEL STATS] Reset to defaults for {profile.ServerName}.");
    }

    private void SyncCatalogSelectionsFromProfile()
    {
        var profile = ActiveProfile;

        foreach (var item in _engramCatalog)
        {
            var configured = profile?.EngramOverrides.FirstOrDefault(x =>
                string.Equals(x.EngramClassName, item.ClassName, StringComparison.OrdinalIgnoreCase));

            item.Selected = configured is not null;
            if (configured is not null)
            {
                item.DefaultPointsCost = configured.PointsCost;
                item.DefaultLevelRequirement = configured.LevelRequirement;
            }
        }

        foreach (var item in _harvestCatalog)
        {
            var configured = profile?.HarvestResourceMultipliers.FirstOrDefault(x =>
                string.Equals(x.ResourceClassName, item.ClassName, StringComparison.OrdinalIgnoreCase));

            item.Selected = configured is not null;
            item.Multiplier = configured?.Multiplier ?? 1.0f;
        }
    }

    private void RefreshEngramCatalogUi()
    {
        var query = EngramSearchBox?.Text?.Trim() ?? string.Empty;
        var items = _engramCatalog
            .Where(x => string.IsNullOrWhiteSpace(query) ||
                        x.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                        x.ClassName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                        x.Category.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(x => x.Selected)
            .ThenBy(x => x.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        EngramCatalogList.ItemsSource = null;
        EngramCatalogList.ItemsSource = items;
        EngramSelectionCountText.Text = $"{_engramCatalog.Count(x => x.Selected)} selected";
    }

    private void LoadCatalogIcons()
    {
        var dataRoot = Path.Combine(AppContext.BaseDirectory, "Data");

        static string ResolveIconPath(string dataRootPath, string iconFile) =>
            Path.Combine(
                dataRootPath,
                iconFile
                    .Replace('/', Path.DirectorySeparatorChar)
                    .Replace('\\', Path.DirectorySeparatorChar));

        foreach (var item in _harvestCatalog)
        {
            if (string.IsNullOrWhiteSpace(item.IconFile))
                continue;

            try
            {
                var path = ResolveIconPath(dataRoot, item.IconFile);
                item.IconBitmap = File.Exists(path) ? new Bitmap(path) : null;
            }
            catch
            {
                item.IconBitmap = null;
            }
        }

        foreach (var engram in _engramCatalog)
        {
            if (string.IsNullOrWhiteSpace(engram.IconFile))
                continue;

            try
            {
                var path = ResolveIconPath(dataRoot, engram.IconFile);
                engram.IconBitmap = File.Exists(path) ? new Bitmap(path) : null;
            }
            catch
            {
                engram.IconBitmap = null;
            }
        }
    }

    private void RefreshHarvestCatalogUi()
    {
        var query = HarvestSearchBox?.Text?.Trim() ?? string.Empty;
        var items = _harvestCatalog
            .Where(x => string.IsNullOrWhiteSpace(query) ||
                        x.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                        x.ClassName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                        x.Category.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                        x.SubCategory.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                        x.Aliases.Any(alias =>
                            alias.Contains(query, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(x => x.Selected)
            .ThenBy(x => x.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        HarvestCatalogList.ItemsSource = null;
        HarvestCatalogList.ItemsSource = items;
        HarvestSelectionCountText.Text = $"{_harvestCatalog.Count(x => x.Selected)} selected";
    }

    private void EngramSearchBox_TextChanged(object? sender, TextChangedEventArgs e) =>
        RefreshEngramCatalogUi();

    private void HarvestSearchBox_TextChanged(object? sender, TextChangedEventArgs e) =>
        RefreshHarvestCatalogUi();

    private async void EngramCatalogCheck_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox check ||
            check.DataContext is not EngramCatalogEntry item ||
            ActiveProfile is not { } profile)
            return;

        item.Selected = check.IsChecked == true;
        var existing = profile.EngramOverrides.FirstOrDefault(x =>
            string.Equals(x.EngramClassName, item.ClassName, StringComparison.OrdinalIgnoreCase));

        if (item.Selected && existing is null)
        {
            profile.EngramOverrides.Add(new EngramOverrideEntry
            {
                EngramClassName = item.ClassName,
                PointsCost = item.DefaultPointsCost,
                LevelRequirement = item.DefaultLevelRequirement
            });
        }
        else if (!item.Selected && existing is not null)
        {
            profile.EngramOverrides.Remove(existing);
        }

        await _settingsService.SaveAsync(_settings);
        RefreshEngramCatalogUi();
    }

    private async void HarvestCatalogCheck_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox check ||
            check.DataContext is not HarvestResourceCatalogEntry item ||
            ActiveProfile is not { } profile)
            return;

        item.Selected = check.IsChecked == true;
        var existing = profile.HarvestResourceMultipliers.FirstOrDefault(x =>
            string.Equals(x.ResourceClassName, item.ClassName, StringComparison.OrdinalIgnoreCase));

        if (item.Selected && existing is null)
        {
            profile.HarvestResourceMultipliers.Add(new HarvestResourceMultiplierEntry
            {
                ResourceClassName = item.ClassName,
                Multiplier = item.Multiplier
            });
        }
        else if (!item.Selected && existing is not null)
        {
            profile.HarvestResourceMultipliers.Remove(existing);
        }

        await _settingsService.SaveAsync(_settings);
        RefreshHarvestCatalogUi();
    }

    private async void HarvestCatalogMultiplierChanged(object? sender, RoutedEventArgs e)
    {
        if (sender is not NumericUpDown control ||
            control.DataContext is not HarvestResourceCatalogEntry item ||
            !item.Selected ||
            ActiveProfile is not { } profile)
            return;

        item.Multiplier = (float)(control.Value ?? 1m);
        var existing = profile.HarvestResourceMultipliers.FirstOrDefault(x =>
            string.Equals(x.ResourceClassName, item.ClassName, StringComparison.OrdinalIgnoreCase));

        if (existing is not null)
        {
            existing.Multiplier = item.Multiplier;
            await _settingsService.SaveAsync(_settings);
        }
    }

    private async void EngramCatalogOptionChanged(object? sender, EventArgs e)
    {
        if (sender is not Control control ||
            control.DataContext is not EngramCatalogEntry item ||
            !item.Selected ||
            ActiveProfile is not { } profile)
            return;

        var existing = profile.EngramOverrides.FirstOrDefault(x =>
            string.Equals(x.EngramClassName, item.ClassName, StringComparison.OrdinalIgnoreCase));

        if (existing is null)
            return;

        // Read the live row controls so only a catalogue-selected class can be persisted.
        if (control.Parent is Grid row)
        {
            var toggles = row.Children.OfType<ToggleSwitch>().ToList();
            var numbers = row.Children.OfType<NumericUpDown>().ToList();

            existing.Hidden = toggles.ElementAtOrDefault(0)?.IsChecked == true;
            existing.RemovePrerequisite = toggles.ElementAtOrDefault(1)?.IsChecked == true;
            existing.PointsCost = (int)(numbers.ElementAtOrDefault(0)?.Value ?? item.DefaultPointsCost);
            existing.LevelRequirement = (int)(numbers.ElementAtOrDefault(1)?.Value ?? item.DefaultLevelRequirement);

            item.DefaultPointsCost = existing.PointsCost;
            item.DefaultLevelRequirement = existing.LevelRequirement;
            await _settingsService.SaveAsync(_settings);
        }
    }

    private void RefreshModsUi()
    {
        var profile = ActiveProfile;
        if (profile is null)
        {
            ModsListBox.ItemsSource = null;
            ModsLaunchPreviewText.Text = "No profile selected";
            ClearModDetails();
            return;
        }

        NormalizeModOrder(profile);

        foreach (var mod in profile.Mods)
        {
            if (string.IsNullOrWhiteSpace(mod.DisplayName))
                mod.DisplayName = $"Mod {mod.ModId}";
        }

        var selectedId = (ModsListBox.SelectedItem as AsaModEntry)?.ModId;

        ModsListBox.ItemsSource = null;
        ModsListBox.ItemsSource = profile.Mods.OrderBy(m => m.LoadOrder).ToList();

        if (!string.IsNullOrWhiteSpace(selectedId))
            ModsListBox.SelectedItem = profile.Mods.FirstOrDefault(m => m.ModId == selectedId);

        var enabledIds = profile.Mods
            .Where(m => m.Enabled)
            .OrderBy(m => m.LoadOrder)
            .Select(m => m.ModId)
            .ToList();

        ModsLaunchPreviewText.Text = enabledIds.Count == 0
            ? "No enabled mods"
            : "-mods=" + string.Join(",", enabledIds);

        ActiveProfileModsText.Text = enabledIds.Count == 1
            ? "1 enabled"
            : $"{enabledIds.Count} enabled";

        if (ModsListBox.SelectedItem is AsaModEntry selected)
            ShowModDetails(selected);
        else
            ClearModDetails();
    }

    private static void NormalizeModOrder(AsaServerProfile profile)
    {
        var ordered = profile.Mods.OrderBy(m => m.LoadOrder).ToList();
        for (var i = 0; i < ordered.Count; i++)
            ordered[i].LoadOrder = i + 1;

        profile.Mods = ordered;
    }

    private async void AddMod_Click(object? sender, RoutedEventArgs e)
    {
        var id = NewModIdBox.Text?.Trim() ?? string.Empty;

        if (!await AddModByIdAsync(id))
            return;

        NewModIdBox.Text = string.Empty;
    }

    private Task<BrowserModBridgeResult> AddModFromBrowserExtensionAsync(string modId)
    {
        var tcs = new TaskCompletionSource<BrowserModBridgeResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                var profile = ActiveProfile;
                if (profile is null)
                {
                    tcs.TrySetResult(new BrowserModBridgeResult(
                        false,
                        "JAASM has no active server profile."));
                    return;
                }

                var alreadyExists = profile.Mods.Any(m =>
                    string.Equals(m.ModId, modId, StringComparison.OrdinalIgnoreCase));

                if (alreadyExists)
                {
                    tcs.TrySetResult(new BrowserModBridgeResult(
                        false,
                        $"Mod {modId} is already in {profile.ServerName}.",
                        true));
                    return;
                }

                var added = await AddModByIdAsync(modId);

                tcs.TrySetResult(new BrowserModBridgeResult(
                    added,
                    added
                        ? $"Added mod {modId} to {profile.ServerName}."
                        : $"JAASM could not add mod {modId}.",
                    false));
            }
            catch (Exception ex)
            {
                tcs.TrySetResult(new BrowserModBridgeResult(
                    false,
                    $"JAASM failed to add mod {modId}: {ex.Message}"));
            }
        });

        return tcs.Task;
    }

    private async Task<bool> AddModByIdAsync(string id)
    {
        var profile = ActiveProfile;
        if (profile is null)
            return false;

        if (string.IsNullOrWhiteSpace(id) || !id.All(char.IsDigit))
        {
            AppendConsole("[MODS] Mod ID must contain digits only.");
            return false;
        }

        if (profile.Mods.Any(m => string.Equals(m.ModId, id, StringComparison.OrdinalIgnoreCase)))
        {
            AppendConsole($"[MODS] Mod {id} already exists in this profile.");
            return false;
        }

        var mod = new AsaModEntry
        {
            ModId = id,
            DisplayName = $"Mod {id}",
            Enabled = true,
            LoadOrder = profile.Mods.Count + 1,
            MetadataStatus = "Querying metadata..."
        };

        profile.Mods.Add(mod);

        AppendConsole($"[MODS] Resolving metadata for project {id}...");
        var resolved = await _curseForgeMods.GetModAsync(id);

        if (resolved is not null)
        {
            ApplyResolvedModMetadata(mod, resolved);
            mod.MetadataStatus = "Metadata current";
            AppendConsole($"[MODS] Resolved {id}: {mod.DisplayName}.");
        }
        else
        {
            mod.MetadataStatus = "Metadata lookup failed";
            AppendConsole($"[MODS] Could not resolve metadata for project {id}; keeping the mod ID.");
        }

        await _settingsService.SaveAsync(_settings);

        var browseMatch = _browseMods.FirstOrDefault(m =>
            string.Equals(m.ModId, id, StringComparison.OrdinalIgnoreCase));

        if (browseMatch is not null)
        {
            browseMatch.IsInstalledInActiveProfile = true;
            browseMatch.IsNotInstalledInActiveProfile = false;
            ModBrowseResultsList.ItemsSource = null;
            ModBrowseResultsList.ItemsSource = _browseMods;
        }

        RefreshModsUi();
        ModsListBox.SelectedItem = mod;
        UpdateLaunchCommandDisplay();
        AppendConsole($"[MODS] Added mod {id} to {profile.ServerName}.");
        return true;
    }

    private void InstallBrowserExtension_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var source = Path.Combine(AppContext.BaseDirectory, "browser-extension");
            if (!Directory.Exists(source))
            {
                ModBrowseStatusText.Text =
                    "Browser extension files are missing from this JAASM build.";
                AppendConsole($"[MOD EXTENSION] Missing bundled extension directory: {source}");
                return;
            }

            var destination = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "JAASM",
                "browser-extension");

            CopyDirectory(source, destination);

            var chrome = FindChromeExecutable();
            if (chrome is not null)
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = chrome,
                    Arguments = "chrome://extensions/",
                    UseShellExecute = true
                });
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = destination,
                UseShellExecute = true
            });

            ModBrowseStatusText.Text =
                "Browser extension prepared. In Chrome, enable Developer mode, click Load unpacked, then select the opened browser-extension folder.";

            AppendConsole($"[MOD EXTENSION] Prepared Chrome extension at: {destination}");
        }
        catch (Exception ex)
        {
            ModBrowseStatusText.Text = "Could not prepare browser extension.";
            AppendConsole($"[MOD EXTENSION] Installation preparation failed: {ex.Message}");
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);

        foreach (var file in Directory.GetFiles(source))
        {
            var target = Path.Combine(destination, Path.GetFileName(file));
            File.Copy(file, target, true);
        }

        foreach (var directory in Directory.GetDirectories(source))
        {
            var target = Path.Combine(destination, Path.GetFileName(directory));
            CopyDirectory(directory, target);
        }
    }

    private static string? FindChromeExecutable()
    {
        if (!OperatingSystem.IsWindows())
            return null;

        var candidates = new[]
        {
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Google", "Chrome", "Application", "chrome.exe")
        };

        return candidates.FirstOrDefault(File.Exists);
    }

    private void OpenCurseForgeSearch_Click(object? sender, RoutedEventArgs e)
    {
        var search = Uri.EscapeDataString(ModBrowseSearchBox.Text?.Trim() ?? string.Empty);
        var url = string.IsNullOrWhiteSpace(search)
            ? "https://www.curseforge.com/ark-survival-ascended/search?class=mods"
            : $"https://www.curseforge.com/ark-survival-ascended/search?class=mods&search={search}";

        OpenExternalModRepository(url, "CurseForge");
    }

    private void OpenArkCodesSearch_Click(object? sender, RoutedEventArgs e)
    {
        var search = Uri.EscapeDataString(ModBrowseSearchBox.Text?.Trim() ?? string.Empty);
        var url = string.IsNullOrWhiteSpace(search)
            ? "https://arkcodes.com/search/"
            : $"https://arkcodes.com/search/?s={search}";

        OpenExternalModRepository(url, "ArkCodes");
    }

    private void OpenExternalModRepository(string url, string provider)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });

            ModBrowseStatusText.Text = $"Opened {provider} in your default browser.";
            AppendConsole($"[MOD BROWSER] Opened {provider}: {url}");
        }
        catch (Exception ex)
        {
            ModBrowseStatusText.Text = $"Could not open {provider}.";
            AppendConsole($"[MOD BROWSER] Failed to open {provider}: {ex.Message}");
        }
    }

    private static void ApplyResolvedModMetadata(AsaModEntry target, AsaModEntry source)
    {
        target.DisplayName = source.DisplayName;
        target.Author = source.Author;
        target.Summary = source.Summary;
        target.Platform = source.Platform;
        target.Downloads = source.Downloads;
        target.Rating = source.Rating;
        target.ThumbsUpCount = source.ThumbsUpCount;
        target.LogoUrl = source.LogoUrl;
        target.WebsiteUrl = source.WebsiteUrl;
        target.PrimaryCategory = source.PrimaryCategory;
        target.LastUpdated = source.LastUpdated;
        target.MainFileId = source.MainFileId;
        target.MainFileName = source.MainFileName;
        target.MainFileSizeBytes = source.MainFileSizeBytes;
        target.ReleaseType = source.ReleaseType;
        target.IsAvailable = source.IsAvailable;
        target.AllowDistribution = source.AllowDistribution;
        target.Dependencies = source.Dependencies.ToList();
    }

    private async void RefreshSelectedModMetadata_Click(object? sender, RoutedEventArgs e)
    {
        var profile = ActiveProfile;
        if (profile is null || ModsListBox.SelectedItem is not AsaModEntry mod)
            return;

        mod.MetadataStatus = "Querying metadata...";
        ShowModDetails(mod);
        AppendConsole($"[MODS] Refreshing metadata for project {mod.ModId}...");

        var resolved = await _curseForgeMods.GetModAsync(mod.ModId);
        if (resolved is null)
        {
            mod.MetadataStatus = "Metadata lookup failed";
            ShowModDetails(mod);
            AppendConsole($"[MODS] Metadata lookup failed for project {mod.ModId}.");
            return;
        }

        ApplyResolvedModMetadata(mod, resolved);
        mod.MetadataStatus = "Metadata current";
        await _settingsService.SaveAsync(_settings);

        RefreshModsUi();
        ModsListBox.SelectedItem = profile.Mods.First(m => m.ModId == mod.ModId);
        AppendConsole($"[MODS] Metadata refreshed: {mod.DisplayName} ({mod.ModId}).");
    }

    private async void RemoveMod_Click(object? sender, RoutedEventArgs e)
    {
        var profile = ActiveProfile;
        if (profile is null || ModsListBox.SelectedItem is not AsaModEntry mod)
            return;

        profile.Mods.RemoveAll(m => string.Equals(m.ModId, mod.ModId, StringComparison.OrdinalIgnoreCase));
        NormalizeModOrder(profile);
        await _settingsService.SaveAsync(_settings);

        RefreshModsUi();
        UpdateLaunchCommandDisplay();
        AppendConsole($"[MODS] Removed mod {mod.ModId} from {profile.ServerName}.");
    }

    private async void MoveModUp_Click(object? sender, RoutedEventArgs e) =>
        await MoveSelectedModAsync(-1);

    private async void MoveModDown_Click(object? sender, RoutedEventArgs e) =>
        await MoveSelectedModAsync(1);

    private async Task MoveSelectedModAsync(int direction)
    {
        var profile = ActiveProfile;
        if (profile is null || ModsListBox.SelectedItem is not AsaModEntry selected)
            return;

        NormalizeModOrder(profile);
        var ordered = profile.Mods.OrderBy(m => m.LoadOrder).ToList();
        var index = ordered.FindIndex(m => m.ModId == selected.ModId);
        var target = index + direction;

        if (index < 0 || target < 0 || target >= ordered.Count)
            return;

        (ordered[index], ordered[target]) = (ordered[target], ordered[index]);
        profile.Mods = ordered;
        NormalizeModOrder(profile);
        await _settingsService.SaveAsync(_settings);

        RefreshModsUi();
        ModsListBox.SelectedItem = profile.Mods.First(m => m.ModId == selected.ModId);
        UpdateLaunchCommandDisplay();
        AppendConsole($"[MODS] Moved mod {selected.ModId} to load order {target + 1}.");
    }

    private async void ToggleModEnabled_Click(object? sender, RoutedEventArgs e)
    {
        var profile = ActiveProfile;
        if (profile is null || ModsListBox.SelectedItem is not AsaModEntry mod)
            return;

        mod.Enabled = !mod.Enabled;
        await _settingsService.SaveAsync(_settings);

        RefreshModsUi();
        ModsListBox.SelectedItem = profile.Mods.First(m => m.ModId == mod.ModId);
        UpdateLaunchCommandDisplay();
        AppendConsole($"[MODS] Mod {mod.ModId} {(mod.Enabled ? "enabled" : "disabled")}.");
    }

    private void ModsListBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (ModsListBox.SelectedItem is AsaModEntry mod)
            ShowModDetails(mod);
        else
            ClearModDetails();
    }

    private void ShowModDetails(AsaModEntry mod)
    {
        ModDetailNameText.Text = string.IsNullOrWhiteSpace(mod.DisplayName)
            ? $"Mod {mod.ModId}"
            : mod.DisplayName;
        ModDetailIdText.Text = $"Mod ID: {mod.ModId}   Load order: {mod.LoadOrder}   Enabled: {(mod.Enabled ? "Yes" : "No")}";
        ModDetailAuthorText.Text = string.IsNullOrWhiteSpace(mod.Author) ? "Author: Unknown" : $"Author: {mod.Author}";
        ModDetailPlatformText.Text = string.IsNullOrWhiteSpace(mod.Platform) ? "Platform: Unknown" : $"Platform: {mod.Platform}";
        ModDetailRatingText.Text = mod.Rating is null
            ? "Rating: Unknown"
            : $"Rating: {mod.Rating:0.0}   Thumbs up: {mod.ThumbsUpCount:N0}";
        ModDetailDownloadsText.Text = mod.Downloads > 0 ? $"Downloads: {mod.Downloads:N0}" : "Downloads: Unknown";
        ModDetailUpdatedText.Text = mod.LastUpdated is null ? "Last updated: Unknown" : $"Last updated: {mod.LastUpdated:yyyy-MM-dd}";
        ModDetailFileText.Text = string.IsNullOrWhiteSpace(mod.MainFileName)
            ? "Main file: Unknown"
            : $"Main file: {mod.MainFileName}" +
              (mod.MainFileId is null ? string.Empty : $"  (File ID {mod.MainFileId})") +
              $"  •  {FormatBytes(mod.MainFileSizeBytes)}" +
              (string.IsNullOrWhiteSpace(mod.PrimaryCategory) ? string.Empty : $"  •  {mod.PrimaryCategory}");
        ModDetailAvailabilityText.Text =
            $"Available: {FormatNullableBool(mod.IsAvailable)}   Distribution allowed: {FormatNullableBool(mod.AllowDistribution)}" +
            (string.IsNullOrWhiteSpace(mod.ReleaseType) ? string.Empty : $"   Release: {mod.ReleaseType}");
        ModDetailDependenciesText.Text = mod.Dependencies.Count == 0
            ? "Dependencies: None known"
            : "Dependencies: " + string.Join(", ", mod.Dependencies);
        ModDetailSummaryText.Text = string.IsNullOrWhiteSpace(mod.Summary)
            ? "No metadata summary loaded."
            : mod.Summary;
        ModMetadataStatusText.Text = $"Metadata: {mod.MetadataStatus}";
    }

    private static string FormatNullableBool(bool? value) =>
        value is null ? "Unknown" : value.Value ? "Yes" : "No";

    private void ClearModDetails()
    {
        ModDetailNameText.Text = "Select a mod";
        ModDetailIdText.Text = string.Empty;
        ModDetailAuthorText.Text = string.Empty;
        ModDetailPlatformText.Text = string.Empty;
        ModDetailRatingText.Text = string.Empty;
        ModDetailDownloadsText.Text = string.Empty;
        ModDetailUpdatedText.Text = string.Empty;
        ModDetailFileText.Text = string.Empty;
        ModDetailAvailabilityText.Text = string.Empty;
        ModDetailDependenciesText.Text = string.Empty;
        ModDetailSummaryText.Text = string.Empty;
        ModMetadataStatusText.Text = "Metadata: not queried";
    }

    private async Task ResolveMissingModMetadataAsync(AsaServerProfile profile)
    {
        var unresolved = profile.Mods
            .Where(m =>
                string.IsNullOrWhiteSpace(m.Author) ||
                m.MetadataStatus is "Not queried" or "Metadata provider unavailable" or "Metadata lookup failed" ||
                m.DisplayName == $"Mod {m.ModId}")
            .ToList();

        if (unresolved.Count == 0)
            return;

        AppendConsole($"[MODS] Resolving metadata for {unresolved.Count} existing mod(s)...");

        foreach (var mod in unresolved)
        {
            mod.MetadataStatus = "Querying metadata...";
            var resolved = await _curseForgeMods.GetModAsync(mod.ModId);

            if (resolved is null)
            {
                mod.MetadataStatus = "Metadata lookup failed";
                AppendConsole($"[MODS] Metadata lookup failed for {mod.ModId}.");
                continue;
            }

            ApplyResolvedModMetadata(mod, resolved);
            mod.MetadataStatus = "Metadata current";
            AppendConsole($"[MODS] Resolved {mod.ModId}: {mod.DisplayName}.");
        }

        await _settingsService.SaveAsync(_settings);
        RefreshModsUi();

        ModUpdatesListBox.ItemsSource = null;
        ModUpdatesListBox.ItemsSource = profile.Mods
            .OrderBy(m => m.LoadOrder)
            .ToList();
    }

    private async void BrowseModsSearch_Click(object? sender, RoutedEventArgs e)
    {
        var sortTag = (ModBrowseSortBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "Popularity";
        var sort = Enum.TryParse<ModBrowseSort>(sortTag, out var parsedSort)
            ? parsedSort
            : ModBrowseSort.Popularity;

        var descending =
            !string.Equals(
                (ModBrowseDirectionBox.SelectedItem as ComboBoxItem)?.Tag?.ToString(),
                "asc",
                StringComparison.OrdinalIgnoreCase);

        var pageSizeText = (ModBrowsePageSizeBox.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        var pageSize = int.TryParse(pageSizeText, out var parsedPageSize) ? parsedPageSize : 50;

        ModBrowseStatusText.Text = _curseForgeMods.IsConfigured
            ? "Searching CurseForge API catalogue..."
            : "Searching public mod catalogue...";

        var result = await _curseForgeMods.SearchAsync(
            new ModBrowseQuery(
                ModBrowseSearchBox.Text ?? string.Empty,
                sort,
                descending,
                pageSize));

        ModBrowseStatusText.Text = result.Message;

        if (!result.Success)
            AppendConsole($"[MOD CATALOGUE] {result.Message}");
        _browseMods = result.Mods.ToList();
        UpdateBrowseInstalledState();
        await LoadBrowseThumbnailsAsync(_browseMods);

        ModBrowseResultsList.ItemsSource = null;
        ModBrowseResultsList.ItemsSource = _browseMods;
        ModBrowseSelectionText.Text = _browseMods.Count == 0
            ? "No browse results."
            : $"{_browseMods.Count} result(s). Select a mod to add it to this server.";
    }

    private void UpdateBrowseInstalledState()
    {
        var installed = ActiveProfile?.Mods
            .Select(m => m.ModId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase)
            ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var mod in _browseMods)
        {
            var isInstalled = installed.Contains(mod.ModId);
            mod.IsInstalledInActiveProfile = isInstalled;
            mod.IsNotInstalledInActiveProfile = !isInstalled;
        }
    }

    private void ModBrowseResultsList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (ModBrowseResultsList.SelectedItem is not AsaModEntry mod)
        {
            ModBrowseSelectionText.Text = "Select a result to add it to this server profile.";
            return;
        }

        var rating = mod.Rating is null ? "unrated" : $"{mod.Rating:0.0}/5";
        var size = FormatBytes(mod.MainFileSizeBytes);
        var updated = mod.LastUpdated?.ToString("yyyy-MM-dd") ?? "unknown date";

        ModBrowseSelectionText.Text =
            $"{mod.DisplayName} • {mod.Downloads:N0} downloads • {rating} • {size} • updated {updated}" +
            (string.IsNullOrWhiteSpace(mod.PrimaryCategory) ? string.Empty : $" • {mod.PrimaryCategory}") +
            (string.IsNullOrWhiteSpace(mod.Platform) ? string.Empty : $" • {mod.Platform}");
    }

    private async void AddBrowseModCard_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.DataContext is not AsaModEntry mod)
            return;

        ModBrowseResultsList.SelectedItem = mod;
        await AddBrowseModEntryAsync(mod);
    }

    private async Task AddBrowseModEntryAsync(AsaModEntry source)
    {
        var profile = ActiveProfile;
        if (profile is null)
            return;

        if (profile.Mods.Any(m => m.ModId == source.ModId))
        {
            ModBrowseSelectionText.Text = $"{source.DisplayName} is already in this server profile.";
            return;
        }

        var mod = new AsaModEntry
        {
            ModId = source.ModId,
            Enabled = true,
            LoadOrder = profile.Mods.Count + 1,
            DisplayName = source.DisplayName,
            Author = source.Author,
            Summary = source.Summary,
            Platform = source.Platform,
            Downloads = source.Downloads,
            Rating = source.Rating,
            ThumbsUpCount = source.ThumbsUpCount,
            LogoUrl = source.LogoUrl,
            WebsiteUrl = source.WebsiteUrl,
            PrimaryCategory = source.PrimaryCategory,
            LastUpdated = source.LastUpdated,
            MainFileId = source.MainFileId,
            MainFileName = source.MainFileName,
            MainFileSizeBytes = source.MainFileSizeBytes,
            ReleaseType = source.ReleaseType,
            IsAvailable = source.IsAvailable,
            AllowDistribution = source.AllowDistribution,
            Dependencies = source.Dependencies.ToList(),
            MetadataStatus = source.MetadataStatus
        };

        profile.Mods.Add(mod);
        NormalizeModOrder(profile);
        await _settingsService.SaveAsync(_settings);

        source.IsInstalledInActiveProfile = true;
        source.IsNotInstalledInActiveProfile = false;
        ModBrowseResultsList.ItemsSource = null;
        ModBrowseResultsList.ItemsSource = _browseMods;

        RefreshModsUi();
        UpdateLaunchCommandDisplay();
        ModBrowseSelectionText.Text = $"Added {mod.DisplayName} to {profile.ServerName}.";
        AppendConsole($"[MODS] Added browsed mod {mod.ModId} ({mod.DisplayName}).");
    }

    private async Task LoadBrowseThumbnailsAsync(IEnumerable<AsaModEntry> mods)
    {
        var gate = new SemaphoreSlim(6);

        var tasks = mods.Select(async mod =>
        {
            if (string.IsNullOrWhiteSpace(mod.LogoUrl))
                return;

            await gate.WaitAsync();
            try
            {
                using var response = await _thumbnailHttp.GetAsync(mod.LogoUrl);
                if (!response.IsSuccessStatusCode)
                    return;

                await using var stream = await response.Content.ReadAsStreamAsync();
                using var ms = new MemoryStream();
                await stream.CopyToAsync(ms);
                ms.Position = 0;

                mod.LogoBitmap = new Bitmap(ms);
            }
            catch
            {
                // A missing/broken thumbnail must never break mod browsing.
            }
            finally
            {
                gate.Release();
            }
        });

        await Task.WhenAll(tasks);
    }

    private async void AddBrowseModToServer_Click(object? sender, RoutedEventArgs e)
    {
        if (ModBrowseResultsList.SelectedItem is not AsaModEntry source)
            return;

        await AddBrowseModEntryAsync(source);
    }

    private async void RefreshInstalledModMetadata_Click(object? sender, RoutedEventArgs e)
    {
        var profile = ActiveProfile;
        if (profile is null)
            return;

        foreach (var installed in profile.Mods)
        {
            var latest = await _curseForgeMods.GetModAsync(installed.ModId);
            if (latest is null)
            {
                installed.MetadataStatus = "Metadata lookup failed";
                continue;
            }

            var previousFile = installed.MainFileId;

            ApplyResolvedModMetadata(installed, latest);

            installed.MetadataStatus =
                previousFile is not null && latest.MainFileId is not null && previousFile != latest.MainFileId
                    ? $"New file available: {latest.MainFileId}"
                    : "Metadata current";
        }

        await _settingsService.SaveAsync(_settings);
        RefreshModsUi();

        ModUpdatesListBox.ItemsSource = null;
        ModUpdatesListBox.ItemsSource = profile.Mods
            .OrderByDescending(m => m.MetadataStatus.StartsWith("New file available", StringComparison.OrdinalIgnoreCase))
            .ThenBy(m => m.LoadOrder)
            .ToList();

        AppendConsole($"[MODS] Refreshed metadata for {profile.Mods.Count} mod(s).");
    }

    private static string FormatBytes(long? bytes)
    {
        if (bytes is null || bytes <= 0)
            return "size unknown";

        double value = bytes.Value;
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        var unit = 0;

        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value:0.##} {units[unit]}";
    }

    private void RefreshPerLevelStatsSummary()
    {
        var profile = ActiveProfile;
        if (profile is null)
        {
            PerLevelStatsSummaryText.Text = "No profile selected";
            return;
        }

        static int CountModified(Dictionary<int, float> values) =>
            values.Count(x => Math.Abs(x.Value - 1.0f) > 0.0001f);

        var modified =
            CountModified(profile.PerLevelStats.Player) +
            CountModified(profile.PerLevelStats.DinoWild) +
            CountModified(profile.PerLevelStats.DinoTamed) +
            CountModified(profile.PerLevelStats.DinoTamedAdd) +
            CountModified(profile.PerLevelStats.DinoTamedAffinity);

        PerLevelStatsSummaryText.Text = modified == 0
            ? "All multipliers at default 1.0"
            : $"{modified} per-level multipliers customized";
    }

    private async void BrowseBackupDestination_Click(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Select JAASM backup destination", AllowMultiple = false });
        if (folders.Count == 0) return;
        var path = folders[0].TryGetLocalPath();
        if (path is null) { BackupStatusText.Text = "JAASM requires a local filesystem directory."; return; }
        _settings.Backup.DestinationDirectory = path;
        BackupDestinationBox.Text = path;
        await _settingsService.SaveAsync(_settings);
        BackupStatusText.Text = $"Backup destination saved: {path}";
    }

    private async void BackupNow_Click(object? sender, RoutedEventArgs e)
    {
        var profile = ActiveProfile;
        if (profile is null) { BackupStatusText.Text = "Select a server profile first."; return; }
        if (string.IsNullOrWhiteSpace(_settings.AsaServerInstallDirectory)) { BackupStatusText.Text = "Configure the ASA installation directory first."; return; }
        if (string.IsNullOrWhiteSpace(_settings.Backup.DestinationDirectory)) { BackupStatusText.Text = "Select a backup destination first."; return; }
        BackupProgress.IsVisible = true;
        BackupStatusText.Text = $"Backing up {profile.ServerName}...";
        try {
            var result = await _backupService.CreateAsync(_settings.AsaServerInstallDirectory, profile, _settings.Backup.DestinationDirectory);
            BackupStatusText.Text = result.Message;
            AppendConsole($"[BACKUP] {result.Message}");
            if (result.Success && result.Sha256 is not null) AppendConsole($"[BACKUP] SHA-256: {result.Sha256}");
        } finally { BackupProgress.IsVisible = false; }
    }

    private async void OpenRestoreBackup_Click(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions {
            Title = "Open JAASM backup", AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("JAASM Backup") { Patterns = new[] { "*.jaasm-backup.zip", "*.zip" } } }
        });
        if (files.Count == 0) return;
        var path = files[0].TryGetLocalPath();
        if (path is null) { RestoreStatusText.Text = "JAASM requires a local backup file."; return; }
        _pendingRestoreArchive = path; _pendingRestoreVerified = false;
        RestoreFileBox.Text = path; VerifyRestoreButton.IsEnabled = true; RestoreSelectedButton.IsEnabled = false;
        RestoreReviewPanel.IsVisible = false; RestoreConfirmationCheck.IsChecked = false;
        RestoreExistingProfileRadio.IsChecked = false; RestoreNewProfileRadio.IsChecked = false;
        RestoreExistingProfileCombo.Items.Clear(); RestoreExistingProfileCombo.SelectedIndex = -1;
        RestoreNewProfileNameBox.Text = string.Empty;
        RestoreStatusText.Text = "Backup opened. Verify it before restore.";
    }

    private async void VerifySelectedBackup_Click(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_pendingRestoreArchive)) return;
        RestoreProgress.IsVisible = true; RestoreStatusText.Text = "Verifying backup and all recorded hashes...";
        try {
            var review = await _backupService.ReviewAsync(_pendingRestoreArchive);
            _pendingRestoreVerified = review.Success; RestoreSelectedButton.IsEnabled = review.Success;
            RestoreStatusText.Text = review.Message;
            if (!review.Success) { RestoreReviewPanel.IsVisible = false; return; }
            RestoreReviewSummaryText.Text =
                $"Server: {review.ServerName}\nMap: {review.Map}\nCreated: {review.CreatedUtc?.ToString("u") ?? "Unknown"}\nFiles: {review.FileCount}\nData size: {review.TotalBytes / 1024d / 1024d:F2} MiB";
            RestoreExistingProfileCombo.Items.Clear();
            foreach (var existingProfile in _settings.AsaProfiles)
                RestoreExistingProfileCombo.Items.Add(new ComboBoxItem { Content = existingProfile.ServerName, Tag = existingProfile.Id });
            RestoreExistingProfileCombo.SelectedIndex = _settings.AsaProfiles.Count > 0 ? 0 : -1;
            RestoreExistingProfileRadio.IsEnabled = _settings.AsaProfiles.Count > 0;
            RestoreExistingProfileRadio.IsChecked = false;
            RestoreNewProfileRadio.IsChecked = true;
            RestoreNewProfileNameBox.Text = review.ServerName;
            RestoreConfigFilesPanel.Children.Clear();
            foreach (var file in await _backupService.ReadConfigurationFilesAsync(_pendingRestoreArchive)) {
                var content = new TextBox { Text = file.Content, IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, MinHeight = 120, MaxHeight = 360 };
                RestoreConfigFilesPanel.Children.Add(new Expander { Header = file.Path, Content = content, IsExpanded = false });
            }
            AppendConsole($"[RESTORE VERIFY] {review.ServerName}: {review.FileCount} files verified.");
        } finally { RestoreProgress.IsVisible = false; }
    }

    private void RestoreSelectedBackup_Click(object? sender, RoutedEventArgs e)
    {
        if (!_pendingRestoreVerified || string.IsNullOrWhiteSpace(_pendingRestoreArchive)) {
            RestoreStatusText.Text = "Verify the opened backup before restore."; return;
        }
        RestoreConfirmationCheck.IsChecked = false;
        RestoreReviewPanel.IsVisible = true;
        RestoreStatusText.Text = "Review the server configuration below, confirm, then Commit Restore.";
    }

    private async void ConfirmRestore_Click(object? sender, RoutedEventArgs e)
    {
        if (!_pendingRestoreVerified || string.IsNullOrWhiteSpace(_pendingRestoreArchive)) { RestoreStatusText.Text = "Open and verify a backup first."; return; }
        if (RestoreConfirmationCheck.IsChecked != true) { RestoreStatusText.Text = "Review confirmation is required before committing restore."; return; }
        if (string.IsNullOrWhiteSpace(_settings.AsaServerInstallDirectory)) { RestoreStatusText.Text = "Configure the ASA installation directory first."; return; }

        var restoreToExisting = RestoreExistingProfileRadio.IsChecked == true;
        var restoreToNew = RestoreNewProfileRadio.IsChecked == true;
        if (!restoreToExisting && !restoreToNew) { RestoreStatusText.Text = "Choose whether to restore to an existing profile or a new profile."; return; }

        AsaServerProfile? targetProfile = null;
        string? preservedExistingName = null;
        if (restoreToExisting)
        {
            if (RestoreExistingProfileCombo.SelectedItem is not ComboBoxItem selectedItem || selectedItem.Tag is not string targetId)
            { RestoreStatusText.Text = "Select the existing server profile to restore into."; return; }
            targetProfile = _settings.AsaProfiles.FirstOrDefault(p => p.Id == targetId);
            if (targetProfile is null) { RestoreStatusText.Text = "The selected target profile no longer exists."; return; }
            if (_asaProcess.GetState(targetProfile.Id).Running) { RestoreStatusText.Text = "Stop the target ARK server before restoring."; return; }
            preservedExistingName = targetProfile.ServerName;
        }
        else
        {
            var newName = RestoreNewProfileNameBox.Text?.Trim();
            if (string.IsNullOrWhiteSpace(newName)) { RestoreStatusText.Text = "Enter a name for the restored profile."; return; }
            if (_settings.AsaProfiles.Any(p => string.Equals(p.ServerName, newName, StringComparison.OrdinalIgnoreCase)))
            { RestoreStatusText.Text = $"A profile named '{newName}' already exists. Select it as an existing target or choose another name."; return; }
        }

        var rollbackDirectory = string.IsNullOrWhiteSpace(_settings.Backup.DestinationDirectory)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JAASM", "rollback")
            : Path.Combine(_settings.Backup.DestinationDirectory, "rollback");
        RestoreProgress.IsVisible = true; RestoreStatusText.Text = "Re-verifying and committing restore...";
        try
        {
            var result = await _backupService.RestoreAsync(_pendingRestoreArchive, _settings.AsaServerInstallDirectory, rollbackDirectory);
            if (!result.Success) { RestoreStatusText.Text = result.Message; AppendConsole($"[RESTORE] {result.Message}"); return; }

            var importedProfile = await _backupService.ReadProfileAsync(_pendingRestoreArchive);
            if (importedProfile is null)
            {
                RestoreStatusText.Text = "Server files were restored, but profile.json could not be imported.";
                AppendConsole("[RESTORE] Files restored but profile import failed.");
                return;
            }

            if (restoreToExisting && targetProfile is not null)
            {
                var index = _settings.AsaProfiles.IndexOf(targetProfile);
                importedProfile.Id = targetProfile.Id;
                importedProfile.ServerName = preservedExistingName!;
                _settings.AsaProfiles[index] = importedProfile;
                _settings.ActiveAsaProfileId = importedProfile.Id;
                AppendConsole($"[RESTORE] Imported backup settings into existing profile '{importedProfile.ServerName}'.");
            }
            else
            {
                importedProfile.Id = Guid.NewGuid().ToString("N");
                importedProfile.ServerName = RestoreNewProfileNameBox.Text!.Trim();
                _settings.AsaProfiles.Add(importedProfile);
                _settings.ActiveAsaProfileId = importedProfile.Id;
                AppendConsole($"[RESTORE] Created restored profile '{importedProfile.ServerName}'.");
            }

            await _settingsService.SaveAsync(_settings);
            RefreshProfileTabs();
            LoadProfileControls();
            RefreshProcessState();
            BackupProfileText.Text = $"Active profile: {ActiveProfile?.ServerName ?? "none"}";

            RestoreStatusText.Text = result.Message;
            AppendConsole($"[RESTORE] {result.Message}");
            RestoreReviewPanel.IsVisible = false; _pendingRestoreArchive = null; _pendingRestoreVerified = false;
            RestoreFileBox.Text = ""; VerifyRestoreButton.IsEnabled = false; RestoreSelectedButton.IsEnabled = false;
        }
        finally { RestoreProgress.IsVisible = false; }
    }

    private void CancelRestore_Click(object? sender, RoutedEventArgs e)
    {
        RestoreConfirmationCheck.IsChecked = false; RestoreReviewPanel.IsVisible = false;
        RestoreStatusText.Text = "Restore cancelled. The verified backup remains open.";
        AppendConsole("[RESTORE] Commit cancelled; no files changed.");
    }

    private void OpenBackupFolder_Click(object? sender, RoutedEventArgs e)
    {
        var path = _settings.Backup.DestinationDirectory;
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) { BackupStatusText.Text = "Backup destination does not exist."; return; }
        try {
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        } catch (Exception ex) { BackupStatusText.Text = $"Could not open backup folder: {ex.Message}"; }
    }

    private async void ApplyStartupSettings_Click(object? sender, RoutedEventArgs e)
    {
        _settings.Startup.StartWithOs = StartWithOsToggle.IsChecked == true;
        _settings.Startup.StartMinimized = StartMinimizedToggle.IsChecked == true;
        _settings.Startup.AutoStartProfileIds = AutoStartProfilesPanel.Children
            .OfType<CheckBox>()
            .Where(x => x.IsChecked == true && x.Tag is string)
            .Select(x => (string)x.Tag!)
            .ToList();
        _settings.Startup.AutoStartActiveServer = false;

        var selectedProfiles = _settings.AsaProfiles.Where(p => _settings.Startup.AutoStartProfileIds.Contains(p.Id)).ToList();
        var conflicts = FindPortConflicts(selectedProfiles);
        if (conflicts.Count > 0)
        {
            StartupStatusText.Text = "Cannot apply startup settings: " + string.Join("; ", conflicts);
            return;
        }

        var result = await _startupService.SetEnabledAsync(_settings.Startup.StartWithOs);
        StartupStatusText.Text = result.Message;
        if (!result.Success) { AppendConsole($"[STARTUP] Failed: {result.Message}"); return; }
        await _settingsService.SaveAsync(_settings);
        AppendConsole($"[STARTUP] {result.Message}");
    }

    private async void DisableStartupService_Click(object? sender, RoutedEventArgs e)
    {
        var result = await _startupService.SetEnabledAsync(false);
        if (result.Success) {
            _settings.Startup.StartWithOs = false;
            StartWithOsToggle.IsChecked = false;
            await _settingsService.SaveAsync(_settings);
        }
        StartupStatusText.Text = result.Message;
        AppendConsole($"[STARTUP] {result.Message}");
    }

    private void RefreshAutoStartProfilesUi()
    {
        AutoStartProfilesPanel.Children.Clear();
        foreach (var profile in _settings.AsaProfiles)
        {
            AutoStartProfilesPanel.Children.Add(new CheckBox
            {
                Content = $"{profile.ServerName}  —  {profile.GamePort} / {profile.QueryPort} / {profile.RconPort}",
                Tag = profile.Id,
                IsChecked = _settings.Startup.AutoStartProfileIds.Contains(profile.Id)
            });
        }
        if (_settings.AsaProfiles.Count == 0)
            AutoStartProfilesPanel.Children.Add(new TextBlock { Text = "No server profiles configured.", Opacity = 0.7 });
    }

    private static List<string> FindPortConflicts(IReadOnlyList<AsaServerProfile> profiles)
    {
        var conflicts = new List<string>();
        for (var i = 0; i < profiles.Count; i++)
        for (var j = i + 1; j < profiles.Count; j++)
        {
            var a = profiles[i]; var b = profiles[j];
            if (a.GamePort == b.GamePort) conflicts.Add($"{a.ServerName} and {b.ServerName}: game port {a.GamePort}");
            if (a.QueryPort == b.QueryPort) conflicts.Add($"{a.ServerName} and {b.ServerName}: query port {a.QueryPort}");
            if (a.RconPort == b.RconPort) conflicts.Add($"{a.ServerName} and {b.ServerName}: RCON port {a.RconPort}");
        }
        return conflicts;
    }

    private async Task AutoStartProfilesAsync()
    {
        var profiles = _settings.AsaProfiles.Where(p => _settings.Startup.AutoStartProfileIds.Contains(p.Id)).ToList();
        var conflicts = FindPortConflicts(profiles);
        if (conflicts.Count > 0)
        {
            AppendConsole("[AUTOSTART] Refused because selected profiles have port conflicts: " + string.Join("; ", conflicts));
            return;
        }
        if (string.IsNullOrWhiteSpace(_settings.AsaServerInstallDirectory)) { AppendConsole("[AUTOSTART] ASA install directory is not configured."); return; }
        var validation = _asaServer.ValidateInstallation(_settings.AsaServerInstallDirectory);
        if (!validation.Success || string.IsNullOrWhiteSpace(validation.ExecutablePath)) { AppendConsole($"[AUTOSTART] {validation.Message}"); return; }

        foreach (var profile in profiles)
        {
            if (_asaProcess.GetState(profile.Id).Running) continue;
            try
            {
                var args = GetEffectiveLaunchArgumentsForProfile(profile);
                var state = await _asaProcess.StartAsync(profile.Id, validation.ExecutablePath, args, CreateConsoleProgress());
                AppendConsole(state.Running ? $"[AUTOSTART] {profile.ServerName} started automatically." : $"[AUTOSTART] {profile.ServerName} did not start: {state.Message}");
            }
            catch (Exception ex) { AppendConsole($"[AUTOSTART] {profile.ServerName} failed: {ex.Message}"); }
        }
        RefreshProcessState();
    }

    private string GetEffectiveLaunchArgumentsForProfile(AsaServerProfile profile)
    {
        if (profile.UseManualLaunchCommand && !string.IsNullOrWhiteSpace(profile.ManualLaunchCommand))
            return profile.ManualLaunchCommand.Trim();
        var args = $"{profile.Map}?listen?SessionName={QuoteUrl(profile.ServerName)}";
        if (!string.IsNullOrWhiteSpace(profile.ServerPassword)) args += $"?ServerPassword={QuoteUrl(profile.ServerPassword)}";
        var saveName = $"JAASM_{profile.Id[..Math.Min(8, profile.Id.Length)]}";
        args += $"?AltSaveDirectoryName={saveName}";
        if (!string.IsNullOrWhiteSpace(profile.AdminPassword)) args += $"?ServerAdminPassword={QuoteUrl(profile.AdminPassword)}";
        args += $" -port={profile.GamePort} -QueryPort={profile.QueryPort} -RCONPort={profile.RconPort} -WinLiveMaxPlayers={profile.MaxPlayers}";
        args += " -ServerPlatform=" + BuildServerPlatformArgument(profile);
        args += $" -log -AltLogDirectoryName=\"{saveName}/Logs\"";
        var mods = profile.Mods.Where(m => m.Enabled).OrderBy(m => m.LoadOrder).Select(m => m.ModId).Where(id => !string.IsNullOrWhiteSpace(id)).ToList();
        if (mods.Count > 0) args += " -mods=" + string.Join(",", mods);
        if (!string.IsNullOrWhiteSpace(profile.ExtraArguments)) args += " " + profile.ExtraArguments;
        return args;
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
