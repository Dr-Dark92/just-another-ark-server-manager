using JAASM.App.Models;

namespace JAASM.App.Services;

public static class ArkCatalogService
{
    // Built-in safe catalogue. Class identifiers are never typed by normal users.
    // Keep this catalogue version-controlled and extend it only with verified ARK identifiers.
    public static List<HarvestResourceCatalogEntry> CreateHarvestResources() =>
        new()
        {
            R("Wood", "PrimalItemResource_Wood_C"),
            R("Thatch", "PrimalItemResource_Thatch_C"),
            R("Stone", "PrimalItemResource_Stone_C"),
            R("Flint", "PrimalItemResource_Flint_C"),
            R("Metal", "PrimalItemResource_Metal_C"),
            R("Metal Ingot", "PrimalItemResource_MetalIngot_C"),
            R("Fiber", "PrimalItemResource_Fibers_C"),
            R("Hide", "PrimalItemResource_Hide_C"),
            R("Chitin", "PrimalItemResource_Chitin_C"),
            R("Keratin", "PrimalItemResource_Keratin_C"),
            R("Crystal", "PrimalItemResource_Crystal_C"),
            R("Obsidian", "PrimalItemResource_Obsidian_C"),
            R("Oil", "PrimalItemResource_Oil_C"),
            R("Silica Pearls", "PrimalItemResource_Silicon_C"),
            R("Black Pearl", "PrimalItemResource_BlackPearl_C"),
            R("Cementing Paste", "PrimalItemResource_ChitinPaste_C"),
            R("Organic Polymer", "PrimalItemResource_Polymer_Organic_C"),
            R("Polymer", "PrimalItemResource_Polymer_C"),
            R("Pelt", "PrimalItemResource_Pelt_C"),
            R("Charcoal", "PrimalItemResource_Charcoal_C"),
            R("Gunpowder", "PrimalItemResource_Gunpowder_C"),
            R("Sparkpowder", "PrimalItemResource_Sparkpowder_C"),
            R("Rare Flower", "PrimalItemResource_RareFlower_C"),
            R("Rare Mushroom", "PrimalItemResource_RareMushroom_C"),
            R("Sap", "PrimalItemResource_Sap_C"),
            R("Sand", "PrimalItemResource_Sand_C"),
            R("Clay", "PrimalItemResource_Clay_C"),
            R("Sulfur", "PrimalItemResource_Sulfur_C"),
            R("Silk", "PrimalItemResource_Silk_C"),
            R("Preserving Salt", "PrimalItemResource_PreservingSalt_C"),
            R("Raw Salt", "PrimalItemResource_RawSalt_C"),
            R("Fungal Wood", "PrimalItemResource_FungalWood_C"),
            R("Green Gem", "PrimalItemResource_Gem_Fertile_C"),
            R("Blue Gem", "PrimalItemResource_Gem_BioLum_C"),
            R("Red Gem", "PrimalItemResource_Gem_Element_C"),
            R("Congealed Gas Ball", "PrimalItemResource_Gas_C"),
            R("Element", "PrimalItemResource_Element_C"),
            R("Element Shard", "PrimalItemResource_ElementShard_C"),
            R("Element Dust", "PrimalItemResource_ElementDust_C"),
            R("Element Ore", "PrimalItemResource_ElementOre_C"),
            R("Scrap Metal", "PrimalItemResource_ScrapMetal_C"),
            R("Scrap Metal Ingot", "PrimalItemResource_ScrapMetalIngot_C")
        };

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

    private static HarvestResourceCatalogEntry R(string name, string className) =>
        new() { DisplayName = name, ClassName = className };

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
