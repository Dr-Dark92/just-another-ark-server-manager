using System.Text.Json;
using JAASM.App.Models;

namespace JAASM.App.Services;

public static class ArkCatalogService
{
    // Built-in safe catalogue. Class identifiers are never typed by normal users.
    // Keep this catalogue version-controlled and extend it only with verified ARK identifiers.
    public static List<HarvestResourceCatalogEntry> CreateHarvestResources()
    {
        var catalogPath = Path.Combine(AppContext.BaseDirectory, "Data", "ark-items.json");

        try
        {
            if (File.Exists(catalogPath))
            {
                var json = File.ReadAllText(catalogPath);
                var root = JsonSerializer.Deserialize<ItemCatalogDocument>(
                    json,
                    new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    });

                if (root?.Items is { Count: > 0 })
                    return root.Items
                        .Where(item => item.HarvestEligible)
                        .ToList();
            }
        }
        catch
        {
            // Fall back to the minimal embedded list below. Catalogue failures must
            // never prevent JAASM from starting.
        }

        return new()
        {
            R("Wood", "PrimalItemResource_Wood_C"),
            R("Thatch", "PrimalItemResource_Thatch_C"),
            R("Stone", "PrimalItemResource_Stone_C"),
            R("Flint", "PrimalItemResource_Flint_C"),
            R("Metal", "PrimalItemResource_Metal_C"),
            R("Fiber", "PrimalItemResource_Fibers_C"),
            R("Hide", "PrimalItemResource_Hide_C"),
            R("Raw Meat", "PrimalItemConsumable_RawMeat_C", "Consumables", "Meat", "meat"),
            R("Raw Prime Meat", "PrimalItemConsumable_RawPrimeMeat_C", "Consumables", "Meat", "meat", "prime"),
            R("Amarberry", "PrimalItemConsumable_Berry_Amarberry_C", "Consumables", "Berries", "berry", "berries"),
            R("Azulberry", "PrimalItemConsumable_Berry_Azulberry_C", "Consumables", "Berries", "berry", "berries"),
            R("Tintoberry", "PrimalItemConsumable_Berry_Tintoberry_C", "Consumables", "Berries", "berry", "berries"),
            R("Mejoberry", "PrimalItemConsumable_Berry_Mejoberry_C", "Consumables", "Berries", "berry", "berries"),
            R("Narcoberry", "PrimalItemConsumable_Berry_Narcoberry_C", "Consumables", "Berries", "berry", "berries"),
            R("Stimberry", "PrimalItemConsumable_Berry_Stimberry_C", "Consumables", "Berries", "berry", "berries")
        };
    }

    public static List<EngramCatalogEntry> CreateEngrams() =>
        new()
        {
            E("Campfire", "EngramEntry_Campfire_C", 3, 2),
            E("Bow", "EngramEntry_Bow_C", 11, 10),
            E("Canteen", "EngramEntry_Canteen_C", 24, 54),
            E("C4 Charge", "EngramEntry_C4Ammo_C", 12, 65),
            E("C4 Remote Detonator", "EngramEntry_WeaponC4_C", 24, 65),
            E("Cannon", "EngramEntry_Cannon_C", 25, 34),
            E("Cannon Ball", "EngramEntry_CannonBall_C", 5, 34),
            E("Camera", "EngramEntry_Camera_C", 30, 50),
            E("Bunk Bed", "EngramEntry_ModernBed_C", 28, 54),
            E("Bug Repellant", "EngramEntry_BugRepel_C", 12, 16),
            E("Bronto Saddle", "EngramEntry_Saddle_Sauro_C", 21, 63),
            E("Bronto Platform Saddle", "EngramEntry_Saddle_Sauro_Platform_C", 35, 82),
            E("Acro Saddle", "EngramEntry_SaddleAcro_C", 77, 43, "ASA"),
            E("Archelon Saddle", "EngramEntry_Saddle_Archelon_ASA_C", 44, 45, "ASA"),
            E("Ceratosaurus Saddle", "EngramEntry_CeratosaurusSaddle_ASA_C", 40, 60, "ASA"),
            E("Decor Box", "EngramEntry_DecorBox_C", 2, 4, "ASA"),
            E("Deinosuchus Saddle", "EngramEntry_Saddle_Deinosuchus_ASA_C", 40, 74, "ASA"),
            E("Deinotherium Saddle", "EngramEntry_SaddleDeinotherium_ASA_C", 50, 85, "ASA"),
            E("Display Case", "EngramEntry_DisplayCase_C", 0, 35, "ASA"),
            E("Dreadnoughtus Platform Saddle", "EngramEntry_DreadSaddle_C", 100, 100, "ASA"),
            E("Fasolasuchus Saddle", "EngramEntry_Saddle_Fasola_C", 40, 70, "ASA"),
            E("Gigantoraptor Saddle", "EngramEntry_Saddle_Gigantoraptor_C", 40, 69, "ASA"),
            E("Hemogoblin Cocktail", "EngramEntry_HemogoblinCocktail_ASA_C", 25, 60, "ASA"),
            E("Medium Tek Teleporter", "EngramEntry_TekTeleporterSmall_C", 0, 0, "ASA"),
            E("Solwyn Saddle", "EngramEntry_LostColony_Saddle_AngelFox_C", 40, 58, "ASA"),
            E("Warbench", "EngramEntry_LostColony_Warbench_C", 21, 64, "ASA"),
            E("Aquarium", "EngramEntry_Fishtank_ToF_C", 5, 26, "Tides of Fortune"),
            E("Bounty Board", "EngramEntry_TOF_BountyBoard_C", 22, 34, "Tides of Fortune"),
            E("Hand Cannon", "EngramEntry_HandCannon_ToF_C", 15, 34, "Tides of Fortune"),
            E("Shipyard", "EngramEntry_TOF_Shipyard_Large_C", 24, 42, "Tides of Fortune"),
            E("Shovel", "EngramEntry_Frontier_Shovel_C", 6, 20, "Tides of Fortune"),
            E("Sloop", "EngramEntry_TOF_Sloop_C", 12, 42, "Tides of Fortune"),
            E("Drake Claw", "EngramEntry_DrakeClaw_C", 25, 56, "Dragontopia"),
            E("Lumina Saddle", "EngramEntry_Saddle_Lumina_C", 18, 65, "Dragontopia"),
            E("Umbra Saddle", "EngramEntry_Dragontopia_Saddle_Umbra_C", 18, 65, "Dragontopia")
        };

    private static HarvestResourceCatalogEntry R(
        string name,
        string className,
        string category = "Resources",
        string subCategory = "",
        params string[] aliases) =>
        new()
        {
            DisplayName = name,
            ClassName = className,
            Category = category,
            SubCategory = subCategory,
            Aliases = aliases.ToList()
        };

    private sealed class ItemCatalogDocument
    {
        public string Source { get; set; } = string.Empty;
        public string GeneratedBy { get; set; } = string.Empty;
        public List<HarvestResourceCatalogEntry> Items { get; set; } = new();
    }

    private static EngramCatalogEntry E(
        string name, string className, int points, int level, string category = "Base Game") =>
        new()
        {
            DisplayName = name,
            ClassName = className,
            DefaultPointsCost = points,
            DefaultLevelRequirement = level,
            Category = category
        };
}
