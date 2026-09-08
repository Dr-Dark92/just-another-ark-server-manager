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
    private readonly AsaConfigService _asaConfig = new();
    private AppSettings _settings = new();
    private bool _loading;
    private AsaServerProfile? ActiveProfile =>
        _settings.AsaProfiles.FirstOrDefault(p => p.Id == _settings.ActiveAsaProfileId);

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
        EnsureProfiles();
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
        ServerPasswordBox.Text = p.ServerPassword;
        AdminPasswordBox.Text = p.AdminPassword;
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
        RefreshModsUi();
        LaunchPreviewText.Text = BuildLaunchArguments();
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
        p.ServerPassword = ServerPasswordBox.Text ?? string.Empty;
        p.AdminPassword = AdminPasswordBox.Text ?? string.Empty;
        p.ExtraArguments = string.Join(" ", p.SelectedExtraArguments);
        ReadCustomizationControls(p.Customization);
        ReadPerLevelStatsControls(p.PerLevelStats);
    }

    private string BuildLaunchArguments()
    {
        var p = ActiveProfile;
        if (p is null)
            return string.Empty;
        var args = $"{p.Map}?SessionName={QuoteUrl(p.ServerName)}?Port={p.GamePort}?QueryPort={p.QueryPort}?RCONPort={p.RconPort}?MaxPlayers={p.MaxPlayers}";

        if (!string.IsNullOrWhiteSpace(p.ServerPassword))
            args += $"?ServerPassword={QuoteUrl(p.ServerPassword)}";
        var saveName = $"JAASM_{p.Id[..Math.Min(8, p.Id.Length)]}";
        args += $"?AltSaveDirectoryName={saveName}";

        if (!string.IsNullOrWhiteSpace(p.AdminPassword))
            args += $"?ServerAdminPassword={QuoteUrl(p.AdminPassword)}";

        args += $" -server -log -AltLogDirectoryName=\"{saveName}/Logs\"";
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
        ExtraArgumentsSummaryText.Text = dialog.Selection.Count == 0
            ? "None selected"
            : $"{dialog.Selection.Count} selected";
        LaunchPreviewText.Text = BuildLaunchArguments();
    }

    private async void SaveProfile_Click(object? sender, RoutedEventArgs e)
    {
        var profile = ActiveProfile;
        if (profile is null)
            return;

        ReadProfileControls();
        await _settingsService.SaveAsync(_settings);
        LaunchPreviewText.Text = BuildLaunchArguments();
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

        var args = BuildLaunchArguments();
        LaunchPreviewText.Text = args;
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

        var args = BuildLaunchArguments();
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
        var profile = ActiveProfile;
        if (profile is null)
            return;

        var id = NewModIdBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(id) || !id.All(char.IsDigit))
        {
            AppendConsole("[MODS] Mod ID must contain digits only.");
            return;
        }

        if (profile.Mods.Any(m => string.Equals(m.ModId, id, StringComparison.OrdinalIgnoreCase)))
        {
            AppendConsole($"[MODS] Mod {id} already exists in this profile.");
            return;
        }

        var mod = new AsaModEntry
        {
            ModId = id,
            DisplayName = $"Mod {id}",
            Enabled = true,
            LoadOrder = profile.Mods.Count + 1
        };

        profile.Mods.Add(mod);
        NewModIdBox.Text = string.Empty;
        await _settingsService.SaveAsync(_settings);

        RefreshModsUi();
        ModsListBox.SelectedItem = mod;
        LaunchPreviewText.Text = BuildLaunchArguments();
        AppendConsole($"[MODS] Added mod {id} to {profile.ServerName}.");
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
        LaunchPreviewText.Text = BuildLaunchArguments();
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
        LaunchPreviewText.Text = BuildLaunchArguments();
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
        LaunchPreviewText.Text = BuildLaunchArguments();
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
        ModDetailDownloadsText.Text = mod.Downloads > 0 ? $"Downloads: {mod.Downloads:N0}" : "Downloads: Unknown";
        ModDetailUpdatedText.Text = mod.LastUpdated is null ? "Last updated: Unknown" : $"Last updated: {mod.LastUpdated:yyyy-MM-dd}";
        ModDetailFileText.Text = string.IsNullOrWhiteSpace(mod.MainFileName)
            ? "Main file: Unknown"
            : $"Main file: {mod.MainFileName}" +
              (mod.MainFileId is null ? string.Empty : $"  (File ID {mod.MainFileId})");
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
        ModDetailDownloadsText.Text = string.Empty;
        ModDetailUpdatedText.Text = string.Empty;
        ModDetailFileText.Text = string.Empty;
        ModDetailAvailabilityText.Text = string.Empty;
        ModDetailDependenciesText.Text = string.Empty;
        ModDetailSummaryText.Text = string.Empty;
        ModMetadataStatusText.Text = "Metadata: not queried";
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
