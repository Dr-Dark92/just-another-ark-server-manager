using System.Globalization;
using System.Text;
using JAASM.App.Models;

namespace JAASM.App.Services;

public sealed record AsaConfigWriteResult(string LivePath, string ProfileSnapshotPath);

public sealed class AsaConfigService
{
    public string GetGameUserSettingsPath(string asaInstallDirectory) =>
        Path.Combine(
            asaInstallDirectory,
            "ShooterGame",
            "Saved",
            "Config",
            "WindowsServer",
            "GameUserSettings.ini");

    public string GetProfileSnapshotPath(string profileId) =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "JAASM",
            "profiles",
            profileId,
            "GameUserSettings.ini");

    public async Task<AsaConfigWriteResult> WriteGameUserSettingsAsync(
        string asaInstallDirectory,
        AsaServerProfile profile,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(asaInstallDirectory))
            throw new ArgumentException("ASA installation directory is not configured.");

        var content = BuildGameUserSettings(profile);

        var snapshotPath = GetProfileSnapshotPath(profile.Id);
        Directory.CreateDirectory(Path.GetDirectoryName(snapshotPath)!);
        await File.WriteAllTextAsync(snapshotPath, content, Encoding.UTF8, ct);

        var livePath = GetGameUserSettingsPath(asaInstallDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(livePath)!);
        await File.WriteAllTextAsync(livePath, content, Encoding.UTF8, ct);

        return new(livePath, snapshotPath);
    }

    public string BuildGameUserSettings(AsaServerProfile profile)
    {
        var c = profile.Customization;
        var sb = new StringBuilder();

        sb.AppendLine("[ServerSettings]");
        Add(sb, "ServerPVE", c.PvE);
        Add(sb, "AllowThirdPersonPlayer", c.AllowThirdPerson);
        Add(sb, "ShowMapPlayerLocation", c.ShowMapPlayerLocation);
        Add(sb, "ServerCrosshair", c.ServerCrosshair);
        Add(sb, "AllowHitMarkers", c.AllowHitMarkers);
        Add(sb, "DisableStructureDecayPvE", c.DisableStructureDecayPvE);
        Add(sb, "DisableDinoDecayPvE", c.DisableDinoDecayPvE);
        Add(sb, "PreventOfflinePvP", c.PreventOfflinePvP);
        Add(sb, "DifficultyOffset", c.DifficultyOffset);
        Add(sb, "OverrideOfficialDifficulty", c.OverrideOfficialDifficulty);

        Add(sb, "XPMultiplier", c.XpMultiplier);
        Add(sb, "TamingSpeedMultiplier", c.TamingSpeedMultiplier);
        Add(sb, "HarvestAmountMultiplier", c.HarvestAmountMultiplier);
        Add(sb, "HarvestHealthMultiplier", c.HarvestHealthMultiplier);
        Add(sb, "PlayerCharacterFoodDrainMultiplier", c.PlayerCharacterFoodDrainMultiplier);
        Add(sb, "PlayerCharacterWaterDrainMultiplier", c.PlayerCharacterWaterDrainMultiplier);
        Add(sb, "DinoCharacterFoodDrainMultiplier", c.DinoCharacterFoodDrainMultiplier);

        Add(sb, "DayCycleSpeedScale", c.DayCycleSpeedScale);
        Add(sb, "DayTimeSpeedScale", c.DayTimeSpeedScale);
        Add(sb, "NightTimeSpeedScale", c.NightTimeSpeedScale);
        Add(sb, "DinoCountMultiplier", c.DinoCountMultiplier);
        Add(sb, "ResourcesRespawnPeriodMultiplier", c.ResourcesRespawnPeriodMultiplier);
        Add(sb, "GlobalSpoilingTimeMultiplier", c.GlobalSpoilingTimeMultiplier);
        Add(sb, "GlobalItemDecompositionTimeMultiplier", c.GlobalItemDecompositionTimeMultiplier);
        Add(sb, "GlobalCorpseDecompositionTimeMultiplier", c.GlobalCorpseDecompositionTimeMultiplier);

        Add(sb, "MatingIntervalMultiplier", c.MatingIntervalMultiplier);
        Add(sb, "EggHatchSpeedMultiplier", c.EggHatchSpeedMultiplier);
        Add(sb, "BabyMatureSpeedMultiplier", c.BabyMatureSpeedMultiplier);
        Add(sb, "BabyCuddleIntervalMultiplier", c.BabyCuddleIntervalMultiplier);
        Add(sb, "BabyImprintAmountMultiplier", c.BabyImprintAmountMultiplier);
        Add(sb, "BabyFoodConsumptionSpeedMultiplier", c.BabyFoodConsumptionSpeedMultiplier);

        Add(sb, "StructureResistanceMultiplier", c.StructureResistanceMultiplier);
        Add(sb, "StructureDamageMultiplier", c.StructureDamageMultiplier);
        Add(sb, "PvEStructureDecayPeriodMultiplier", c.PvEStructureDecayPeriodMultiplier);
        Add(sb, "PvEDinoDecayPeriodMultiplier", c.PvEDinoDecayPeriodMultiplier);
        Add(sb, "AllowCaveBuildingPvE", c.AllowCaveBuildingPvE);
        Add(sb, "EnableExtraStructurePreventionVolumes", c.EnableExtraStructurePreventionVolumes);
        Add(sb, "PvPStructureDecay", c.PvPStructureDecay);

        Add(sb, "PlayerDamageMultiplier", c.PlayerDamageMultiplier);
        Add(sb, "PlayerResistanceMultiplier", c.PlayerResistanceMultiplier);
        Add(sb, "DinoDamageMultiplier", c.DinoDamageMultiplier);
        Add(sb, "DinoResistanceMultiplier", c.DinoResistanceMultiplier);
        Add(sb, "PlayerStaminaDrainMultiplier", c.PlayerStaminaDrainMultiplier);
        Add(sb, "DinoStaminaDrainMultiplier", c.DinoStaminaDrainMultiplier);
        Add(sb, "PlayerHealthRecoveryMultiplier", c.PlayerHealthRecoveryMultiplier);
        Add(sb, "DinoHealthRecoveryMultiplier", c.DinoHealthRecoveryMultiplier);

        return sb.ToString();
    }

    private static void Add(StringBuilder sb, string key, bool value) =>
        sb.AppendLine($"{key}={value.ToString().ToLowerInvariant()}");

    private static void Add(StringBuilder sb, string key, float value) =>
        sb.AppendLine($"{key}={value.ToString("0.###", CultureInfo.InvariantCulture)}");
}
