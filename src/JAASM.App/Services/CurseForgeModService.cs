using System.Net.Http.Json;
using System.Text.Json;
using JAASM.App.Models;

namespace JAASM.App.Services;

public enum ModBrowseSort
{
    Popularity,
    LastUpdated,
    Name,
    Author,
    Downloads,
    ReleasedDate,
    Rating,
    Size
}

public sealed record ModBrowseQuery(
    string Search,
    ModBrowseSort Sort,
    bool Descending,
    int PageSize = 50);

public sealed record ModBrowseResponse(
    bool Success,
    string Message,
    IReadOnlyList<AsaModEntry> Mods);

public sealed class CurseForgeModService
{
    public const int AsaGameId = 83374;
    private const string ApiBase = "https://api.curseforge.com/v1";
    private readonly HttpClient _http;

    public CurseForgeModService()
    {
        _http = new HttpClient();
        var apiKey = Environment.GetEnvironmentVariable("JAASM_CURSEFORGE_API_KEY");
        if (!string.IsNullOrWhiteSpace(apiKey))
            _http.DefaultRequestHeaders.TryAddWithoutValidation("x-api-key", apiKey);
    }

    public bool IsConfigured =>
        _http.DefaultRequestHeaders.Contains("x-api-key");

    public async Task<ModBrowseResponse> SearchAsync(
        ModBrowseQuery query,
        CancellationToken ct = default)
    {
        if (!IsConfigured)
        {
            return new(false,
                "CurseForge catalogue is not configured. JAASM core mod loading still works. " +
                "For development, set JAASM_CURSEFORGE_API_KEY to an application-level CurseForge key.",
                Array.Empty<AsaModEntry>());
        }

        try
        {
            var size = Math.Clamp(query.PageSize, 1, 50);
            var uri = $"{ApiBase}/mods/search?gameId={AsaGameId}&pageSize={size}";

            if (!string.IsNullOrWhiteSpace(query.Search))
                uri += "&searchFilter=" + Uri.EscapeDataString(query.Search.Trim());

            // CurseForge does not expose file-size as a server-side sort field.
            // For Size, request a stable useful set and sort locally after parsing.
            var sort = query.Sort == ModBrowseSort.Size
                ? ModBrowseSort.Downloads
                : query.Sort;

            uri += $"&sortField={MapSortField(sort)}&sortOrder={(query.Descending ? "desc" : "asc")}";

            using var response = await _http.GetAsync(uri, ct);
            var json = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
                return new(false,
                    $"CurseForge returned HTTP {(int)response.StatusCode}.",
                    Array.Empty<AsaModEntry>());

            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("data", out var data) ||
                data.ValueKind != JsonValueKind.Array)
            {
                return new(false, "CurseForge response did not contain a mod list.",
                    Array.Empty<AsaModEntry>());
            }

            var mods = new List<AsaModEntry>();

            foreach (var item in data.EnumerateArray())
            {
                var mod = ParseMod(item);
                mods.Add(mod);
            }

            IEnumerable<AsaModEntry> ordered = mods;
            if (query.Sort == ModBrowseSort.Size)
            {
                ordered = query.Descending
                    ? mods.OrderByDescending(m => m.MainFileSizeBytes ?? 0)
                    : mods.OrderBy(m => m.MainFileSizeBytes ?? 0);
            }

            return new(true, $"Loaded {mods.Count} mods from CurseForge.", ordered.ToList());
        }
        catch (Exception ex)
        {
            return new(false, $"CurseForge browse failed: {ex.Message}",
                Array.Empty<AsaModEntry>());
        }
    }

    private static AsaModEntry ParseMod(JsonElement item)
    {
        var id = item.TryGetProperty("id", out var idEl) ? idEl.GetInt32().ToString() : string.Empty;
        var name = GetString(item, "name");
        var summary = GetString(item, "summary");
        var downloads = item.TryGetProperty("downloadCount", out var dlEl) && dlEl.TryGetInt64(out var dl) ? dl : 0;
        var rating = item.TryGetProperty("rating", out var ratingEl) && ratingEl.ValueKind == JsonValueKind.Number
            ? ratingEl.GetDouble()
            : (double?)null;
        var thumbs = item.TryGetProperty("thumbsUpCount", out var thumbsEl) && thumbsEl.TryGetInt32(out var t) ? t : 0;
        var available = item.TryGetProperty("isAvailable", out var availEl) && availEl.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? availEl.GetBoolean()
            : (bool?)null;
        var distribution = item.TryGetProperty("allowModDistribution", out var distEl) &&
                           distEl.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? distEl.GetBoolean()
            : (bool?)null;

        DateTimeOffset? updated = null;
        if (DateTimeOffset.TryParse(GetString(item, "dateModified"), out var parsed))
            updated = parsed;

        var author = string.Empty;
        if (item.TryGetProperty("authors", out var authors) &&
            authors.ValueKind == JsonValueKind.Array &&
            authors.GetArrayLength() > 0)
        {
            author = GetString(authors[0], "name");
        }

        var category = string.Empty;
        if (item.TryGetProperty("categories", out var categories) &&
            categories.ValueKind == JsonValueKind.Array &&
            categories.GetArrayLength() > 0)
        {
            category = GetString(categories[0], "name");
        }

        var logo = string.Empty;
        if (item.TryGetProperty("logo", out var logoEl) && logoEl.ValueKind == JsonValueKind.Object)
            logo = GetString(logoEl, "thumbnailUrl");

        var website = string.Empty;
        if (item.TryGetProperty("links", out var links) && links.ValueKind == JsonValueKind.Object)
            website = GetString(links, "websiteUrl");

        JsonElement? chosenFile = null;
        if (item.TryGetProperty("latestFiles", out var files) && files.ValueKind == JsonValueKind.Array)
        {
            foreach (var file in files.EnumerateArray())
            {
                var filename = GetString(file, "fileName");
                if (filename.Contains("windowsserver", StringComparison.OrdinalIgnoreCase))
                {
                    chosenFile = file;
                    break;
                }

                chosenFile ??= file;
            }
        }

        int? fileId = null;
        string fileName = string.Empty;
        long? fileSize = null;
        string release = string.Empty;
        var dependencies = new List<string>();

        if (chosenFile is { } cf)
        {
            if (cf.TryGetProperty("id", out var fid) && fid.TryGetInt32(out var fi))
                fileId = fi;
            fileName = GetString(cf, "fileName");
            if (cf.TryGetProperty("fileLength", out var fl) && fl.TryGetInt64(out var bytes))
                fileSize = bytes;

            if (cf.TryGetProperty("releaseType", out var rt) && rt.TryGetInt32(out var releaseType))
                release = releaseType switch { 1 => "Release", 2 => "Beta", 3 => "Alpha", _ => releaseType.ToString() };

            if (cf.TryGetProperty("dependencies", out var deps) && deps.ValueKind == JsonValueKind.Array)
            {
                foreach (var dep in deps.EnumerateArray())
                {
                    if (dep.TryGetProperty("modId", out var depId) && depId.TryGetInt32(out var di))
                        dependencies.Add(di.ToString());
                }
            }
        }

        return new AsaModEntry
        {
            ModId = id,
            DisplayName = string.IsNullOrWhiteSpace(name) ? $"Mod {id}" : name,
            Author = author,
            Summary = summary,
            PrimaryCategory = category,
            Platform = InferPlatform(fileName),
            Downloads = downloads,
            Rating = rating,
            ThumbsUpCount = thumbs,
            LogoUrl = logo,
            WebsiteUrl = website,
            LastUpdated = updated,
            MainFileId = fileId,
            MainFileName = fileName,
            MainFileSizeBytes = fileSize,
            ReleaseType = release,
            IsAvailable = available,
            AllowDistribution = distribution,
            Dependencies = dependencies,
            MetadataStatus = "Loaded from CurseForge"
        };
    }

    private static string InferPlatform(string fileName)
    {
        if (fileName.Contains("windowsserver", StringComparison.OrdinalIgnoreCase))
            return "Windows Server";
        if (fileName.Contains("windows", StringComparison.OrdinalIgnoreCase))
            return "Windows / Cross-platform package";
        return string.IsNullOrWhiteSpace(fileName) ? "Unknown" : "Package-specific";
    }

    private static string GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static int MapSortField(ModBrowseSort sort) => sort switch
    {
        ModBrowseSort.Popularity => 2,
        ModBrowseSort.LastUpdated => 3,
        ModBrowseSort.Name => 4,
        ModBrowseSort.Author => 5,
        ModBrowseSort.Downloads => 6,
        ModBrowseSort.ReleasedDate => 11,
        ModBrowseSort.Rating => 12,
        _ => 6
    };
}
