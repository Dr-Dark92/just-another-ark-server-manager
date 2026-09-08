namespace JAASM.App.Models;

public sealed class AppSettings
{
    public string? SteamCmdPath { get; set; }
    public string? SteamCmdInstallDirectory { get; set; }
    public string? AsaServerInstallDirectory { get; set; }

    // Legacy single-profile value retained only for settings migration.
    public AsaServerProfile AsaProfile { get; set; } = new();

    public List<AsaServerProfile> AsaProfiles { get; set; } = new();
    public string? ActiveAsaProfileId { get; set; }
    public WebGuiSettings WebGui { get; set; } = new();
}

public sealed class AsaServerProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string ServerName { get; set; } = "ARK Server";
    public string Map { get; set; } = "TheIsland_WP";
    public int MaxPlayers { get; set; } = 70;
    public int GamePort { get; set; } = 7777;
    public int QueryPort { get; set; } = 27015;
    public int RconPort { get; set; } = 27020;
    public string ServerPassword { get; set; } = string.Empty;
    public string AdminPassword { get; set; } = string.Empty;
    public string ExtraArguments { get; set; } = string.Empty;
    public List<string> SelectedExtraArguments { get; set; } = new();
    public AsaCustomizationSettings Customization { get; set; } = new();
    public PerLevelStatSettings PerLevelStats { get; set; } = new();
    public List<AsaModEntry> Mods { get; set; } = new();
}

public sealed class AsaModEntry
{
    public string ModId { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public int LoadOrder { get; set; }

    // Optional metadata. Core mod loading does not depend on these values.
    public string DisplayName { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public string Platform { get; set; } = string.Empty;
    public long Downloads { get; set; }
    public DateTimeOffset? LastUpdated { get; set; }
    public int? MainFileId { get; set; }
    public string MainFileName { get; set; } = string.Empty;
    public long? MainFileSizeBytes { get; set; }
    public string ReleaseType { get; set; } = string.Empty;
    public bool? IsAvailable { get; set; }
    public bool? AllowDistribution { get; set; }
    public List<string> Dependencies { get; set; } = new();
    public string MetadataStatus { get; set; } = "Not queried";
}

public sealed class PerLevelStatSettings
{
    public Dictionary<int, float> Player { get; set; } = CreateDefaults();
    public Dictionary<int, float> DinoWild { get; set; } = CreateDefaults();
    public Dictionary<int, float> DinoTamed { get; set; } = CreateDefaults();
    public Dictionary<int, float> DinoTamedAdd { get; set; } = CreateDefaults();
    public Dictionary<int, float> DinoTamedAffinity { get; set; } = CreateDefaults();

    private static Dictionary<int, float> CreateDefaults() =>
        Enumerable.Range(0, 12).ToDictionary(i => i, _ => 1.0f);
}

public sealed class AsaCustomizationSettings
{
    // General
    public bool PvE { get; set; } = true;
    public bool AllowThirdPerson { get; set; } = true;
    public bool ShowMapPlayerLocation { get; set; } = true;
    public bool ServerCrosshair { get; set; } = true;
    public bool AllowHitMarkers { get; set; } = true;
    public bool DisableStructureDecayPvE { get; set; }
    public bool DisableDinoDecayPvE { get; set; }
    public bool PreventOfflinePvP { get; set; }
    public float DifficultyOffset { get; set; } = 1.0f;
    public float OverrideOfficialDifficulty { get; set; } = 5.0f;

    // Rates
    public float XpMultiplier { get; set; } = 1.0f;
    public float TamingSpeedMultiplier { get; set; } = 1.0f;
    public float HarvestAmountMultiplier { get; set; } = 1.0f;
    public float HarvestHealthMultiplier { get; set; } = 1.0f;
    public float PlayerCharacterFoodDrainMultiplier { get; set; } = 1.0f;
    public float PlayerCharacterWaterDrainMultiplier { get; set; } = 1.0f;
    public float DinoCharacterFoodDrainMultiplier { get; set; } = 1.0f;

    // World
    public float DayCycleSpeedScale { get; set; } = 1.0f;
    public float DayTimeSpeedScale { get; set; } = 1.0f;
    public float NightTimeSpeedScale { get; set; } = 1.0f;
    public float DinoCountMultiplier { get; set; } = 1.0f;
    public float ResourcesRespawnPeriodMultiplier { get; set; } = 1.0f;
    public float GlobalSpoilingTimeMultiplier { get; set; } = 1.0f;
    public float GlobalItemDecompositionTimeMultiplier { get; set; } = 1.0f;
    public float GlobalCorpseDecompositionTimeMultiplier { get; set; } = 1.0f;

    // Breeding
    public float MatingIntervalMultiplier { get; set; } = 1.0f;
    public float EggHatchSpeedMultiplier { get; set; } = 1.0f;
    public float BabyMatureSpeedMultiplier { get; set; } = 1.0f;
    public float BabyCuddleIntervalMultiplier { get; set; } = 1.0f;
    public float BabyImprintAmountMultiplier { get; set; } = 1.0f;
    public float BabyFoodConsumptionSpeedMultiplier { get; set; } = 1.0f;

    // Structures
    public float StructureResistanceMultiplier { get; set; } = 1.0f;
    public float StructureDamageMultiplier { get; set; } = 1.0f;
    public float PvEStructureDecayPeriodMultiplier { get; set; } = 1.0f;
    public float PvEDinoDecayPeriodMultiplier { get; set; } = 1.0f;
    public bool AllowCaveBuildingPvE { get; set; }
    public bool EnableExtraStructurePreventionVolumes { get; set; } = true;
    public bool PvPStructureDecay { get; set; } = true;

    // Player and creature stats
    public float PlayerDamageMultiplier { get; set; } = 1.0f;
    public float PlayerResistanceMultiplier { get; set; } = 1.0f;
    public float DinoDamageMultiplier { get; set; } = 1.0f;
    public float DinoResistanceMultiplier { get; set; } = 1.0f;
    public float PlayerStaminaDrainMultiplier { get; set; } = 1.0f;
    public float DinoStaminaDrainMultiplier { get; set; } = 1.0f;
    public float PlayerHealthRecoveryMultiplier { get; set; } = 1.0f;
    public float DinoHealthRecoveryMultiplier { get; set; } = 1.0f;
}

public sealed class WebGuiSettings
{
    public bool Enabled { get; set; }
    public string ListenAddress { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 8484;
}
