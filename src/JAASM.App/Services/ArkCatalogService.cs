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

    public static List<EngramCatalogEntry> CreateEngrams()
    {
        var catalogPath = Path.Combine(AppContext.BaseDirectory, "Data", "ark-engrams.json");

        try
        {
            if (File.Exists(catalogPath))
            {
                var json = File.ReadAllText(catalogPath);
                var root = JsonSerializer.Deserialize<EngramCatalogDocument>(
                    json,
                    new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    });

                if (root?.Engrams is { Count: > 0 })
                    return root.Engrams;
            }
        }
        catch
        {
            // The runtime remains offline. If the snapshot is corrupt/missing,
            // fail soft to a tiny emergency fallback rather than querying the web.
        }

        return new()
        {
            E("Campfire", "EngramEntry_Campfire_C", 3, 2),
            E("Bow", "EngramEntry_Bow_C", 11, 10),
            E("Acro Saddle", "EngramEntry_SaddleAcro_C", 77, 43, "ASA")
        };
    }

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

    private sealed class EngramCatalogDocument
    {
        public string Source { get; set; } = string.Empty;
        public string SnapshotDate { get; set; } = string.Empty;
        public bool RuntimeNetworkRequired { get; set; }
        public List<EngramCatalogEntry> Engrams { get; set; } = new();
    }

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
