using System.Globalization;
using System.Text;
using JAASM.App.Models;

namespace JAASM.App.Services;

public sealed record AsaConfigWriteResult(
    string LivePath,
    string ProfileSnapshotPath,
    string LiveGameIniPath,
    string ProfileGameIniSnapshotPath);

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

    public string GetGameIniPath(string asaInstallDirectory) =>
        Path.Combine(
            asaInstallDirectory,
            "ShooterGame",
            "Saved",
            "Config",
            "WindowsServer",
            "Game.ini");

    public string GetGameIniSnapshotPath(string profileId) =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "JAASM",
            "profiles",
            profileId,
            "Game.ini");

    public async Task<AsaConfigWriteResult> WriteGameUserSettingsAsync(
        string asaInstallDirectory,
        AsaServerProfile profile,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(asaInstallDirectory))
            throw new ArgumentException("ASA installation directory is not configured.");

        var content = BuildGameUserSettings(profile);
        var gameIniContent = BuildGameIni(profile);

        var snapshotPath = GetProfileSnapshotPath(profile.Id);
        Directory.CreateDirectory(Path.GetDirectoryName(snapshotPath)!);
        await File.WriteAllTextAsync(snapshotPath, content, Encoding.UTF8, ct);

        var livePath = GetGameUserSettingsPath(asaInstallDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(livePath)!);
        await File.WriteAllTextAsync(livePath, content, Encoding.UTF8, ct);

        var gameSnapshotPath = GetGameIniSnapshotPath(profile.Id);
        Directory.CreateDirectory(Path.GetDirectoryName(gameSnapshotPath)!);
        await File.WriteAllTextAsync(gameSnapshotPath, gameIniContent, Encoding.UTF8, ct);

        var liveGameIniPath = GetGameIniPath(asaInstallDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(liveGameIniPath)!);
        await File.WriteAllTextAsync(liveGameIniPath, gameIniContent, Encoding.UTF8, ct);

        return new(livePath, snapshotPath, liveGameIniPath, gameSnapshotPath);
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

    public string BuildGameIni(AsaServerProfile profile)
    {
        var sb = new StringBuilder();
        sb.AppendLine("[/script/shootergame.shootergamemode]");

        AddPerLevel(sb, "PerLevelStatsMultiplier_Player", profile.PerLevelStats.Player);
        AddPerLevel(sb, "PerLevelStatsMultiplier_DinoWild", profile.PerLevelStats.DinoWild);
        AddPerLevel(sb, "PerLevelStatsMultiplier_DinoTamed", profile.PerLevelStats.DinoTamed);
        AddPerLevel(sb, "PerLevelStatsMultiplier_DinoTamed_Add", profile.PerLevelStats.DinoTamedAdd);
        AddPerLevel(sb, "PerLevelStatsMultiplier_DinoTamed_Affinity", profile.PerLevelStats.DinoTamedAffinity);

        foreach (var engram in profile.EngramOverrides
                     .Where(x => !string.IsNullOrWhiteSpace(x.EngramClassName)))
        {
            var className = EscapeIniTupleValue(engram.EngramClassName.Trim());
            sb.AppendLine(
                "OverrideNamedEngramEntries=(" +
                $"EngramClassName=\"{className}\"," +
                $"EngramHidden={engram.Hidden.ToString().ToLowerInvariant()}," +
                $"EngramPointsCost={Math.Max(0, engram.PointsCost)}," +
                $"EngramLevelRequirement={Math.Max(0, engram.LevelRequirement)}," +
                $"RemoveEngramPreReq={engram.RemovePrerequisite.ToString().ToLowerInvariant()})");
        }

        foreach (var harvest in profile.HarvestResourceMultipliers
                     .Where(x => !string.IsNullOrWhiteSpace(x.ResourceClassName)))
        {
            var className = EscapeIniTupleValue(harvest.ResourceClassName.Trim());
            var multiplier = Math.Max(0f, harvest.Multiplier)
                .ToString("0.###", CultureInfo.InvariantCulture);

            sb.AppendLine(
                "HarvestResourceItemAmountClassMultipliers=(" +
                $"ClassName=\"{className}\"," +
                $"Multiplier={multiplier})");
        }

        return sb.ToString();
    }

    private static string EscapeIniTupleValue(string value) =>
        value.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private static void AddPerLevel(
        StringBuilder sb,
        string prefix,
        Dictionary<int, float> values)
    {
        foreach (var pair in values.OrderBy(x => x.Key))
        {
            sb.AppendLine(
                $"{prefix}[{pair.Key}]={pair.Value.ToString("0.###", CultureInfo.InvariantCulture)}");
        }
    }

    private static void Add(StringBuilder sb, string key, bool value) =>
        sb.AppendLine($"{key}={value.ToString().ToLowerInvariant()}");

    private static void Add(StringBuilder sb, string key, float value) =>
        sb.AppendLine($"{key}={value.ToString("0.###", CultureInfo.InvariantCulture)}");
}
