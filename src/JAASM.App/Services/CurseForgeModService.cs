using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
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
        _http = new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.All
        });
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
            "(KHTML, like Gecko) Chrome/152.0.0.0 Safari/537.36");
        _http.DefaultRequestHeaders.Accept.ParseAdd(
            "text/html,application/xhtml+xml,application/json;q=0.9,*/*;q=0.8");
        _http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
        ConfigureApiKey(Environment.GetEnvironmentVariable("JAASM_CURSEFORGE_API_KEY"));
    }

    public bool IsConfigured =>
        _http.DefaultRequestHeaders.Contains("x-api-key");

    public void ConfigureApiKey(string? apiKey)
    {
        _http.DefaultRequestHeaders.Remove("x-api-key");

        if (!string.IsNullOrWhiteSpace(apiKey))
            _http.DefaultRequestHeaders.TryAddWithoutValidation("x-api-key", apiKey.Trim());
    }

    public async Task<AsaModEntry?> GetModAsync(string modId, CancellationToken ct = default)
    {
        if (!int.TryParse(modId, out var id))
            return null;

        // Preferred path: official CurseForge API when a project key is configured.
        if (IsConfigured)
        {
            try
            {
                using var response = await _http.GetAsync($"{ApiBase}/mods/{id}", ct);
                if (response.IsSuccessStatusCode)
                {
                    var json = await response.Content.ReadAsStringAsync(ct);
                    using var doc = JsonDocument.Parse(json);

                    if (doc.RootElement.TryGetProperty("data", out var data) &&
                        data.ValueKind == JsonValueKind.Object)
                        return ParseMod(data);
                }
            }
            catch
            {
                // Fall through to public metadata fallback.
            }
        }

        // Public fallback: ArkCodes mirrors useful ASA project metadata keyed by
        // the CurseForge numeric project ID. This keeps Add-by-ID useful without
        // forcing every gamer to obtain a CurseForge developer API key.
        return await GetModFromPublicMetadataAsync(modId, ct);
    }

    public async Task<ModBrowseResponse> SearchAsync(
        ModBrowseQuery query,
        CancellationToken ct = default)
    {
        if (!IsConfigured)
            return await SearchPublicAsaIndexAsync(query, ct);

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

    private async Task<AsaModEntry?> GetModFromPublicMetadataAsync(
        string modId,
        CancellationToken ct)
    {
        try
        {
            var url = $"https://arkcodes.com/mods/{modId}/";
            using var response = await _http.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode)
                return null;

            var html = await response.Content.ReadAsStringAsync(ct);
            var text = HtmlToText(html);

            var parsedId = MatchValue(text, @"Mod ID\s*:?\s*([0-9]+)");
            if (!string.Equals(parsedId, modId, StringComparison.OrdinalIgnoreCase))
                return null;

            var title = HtmlMatch(html, @"<h1[^>]*>(.*?)</h1>");
            var author = MatchValue(text, @"Author\s*:?\s*([^\r\n]+)");
            var downloadsText = MatchValue(text, @"Downloads\s*:?\s*([0-9,\.]+[KMB]?)");
            var fileSizeText = MatchValue(text, @"File Size\s*:?\s*([0-9\.]+\s*(?:KB|MB|GB|TB|B))");
            var updatedText = MatchValue(text, @"Updated\s*:?\s*([0-9]{4}-[0-9]{2}-[0-9]{2})");
            var category = MatchValue(text, @"Categories?\s*:?\s*([^\r\n]+)");
            var summary = ExtractFirstParagraphAfterHeading(html, "Description");

            return new AsaModEntry
            {
                ModId = modId,
                DisplayName = string.IsNullOrWhiteSpace(title) ? $"Mod {modId}" : title,
                Author = author,
                Summary = summary,
                Platform = title.Contains("Crossplay", StringComparison.OrdinalIgnoreCase)
                    ? "Cross-Platform"
                    : "Unknown",
                Downloads = ParseCompactNumber(downloadsText),
                PrimaryCategory = category,
                LastUpdated = DateTimeOffset.TryParse(updatedText, out var updated) ? updated : null,
                MainFileSizeBytes = ParseSizeBytes(fileSizeText),
                IsAvailable = true,
                AllowDistribution = null,
                WebsiteUrl = response.RequestMessage?.RequestUri?.ToString() ?? url,
                MetadataStatus = "Loaded from public ASA metadata"
            };
        }
        catch
        {
            return null;
        }
    }

    private async Task<ModBrowseResponse> SearchPublicAsaIndexAsync(
        ModBrowseQuery query,
        CancellationToken ct)
    {
        try
        {
            var pageSize = Math.Clamp(query.PageSize, 1, 50);
            var search = query.Search?.Trim() ?? string.Empty;

            // CurseForge's public website currently rejects non-browser catalogue
            // requests with HTTP 403. ArkCodes exposes a public ASA mod index and
            // individual numeric project pages, so use it as the no-key discovery
            // source. The official CurseForge API remains the preferred path when
            // a JAASM application key is configured.
            var candidates = new[]
            {
                string.IsNullOrWhiteSpace(search)
                    ? "https://arkcodes.com/mods/"
                    : $"https://arkcodes.com/mods/?search={Uri.EscapeDataString(search)}",
                string.IsNullOrWhiteSpace(search)
                    ? "https://arkcodes.com/mods/"
                    : $"https://arkcodes.com/?s={Uri.EscapeDataString(search)}"
            };

            string? html = null;
            string? usedUrl = null;
            HttpStatusCode lastStatus = 0;

            foreach (var url in candidates.Distinct())
            {
                using var response = await _http.GetAsync(url, ct);
                lastStatus = response.StatusCode;
                if (!response.IsSuccessStatusCode)
                    continue;

                var body = await response.Content.ReadAsStringAsync(ct);
                if (body.Contains("/mods/", StringComparison.OrdinalIgnoreCase))
                {
                    html = body;
                    usedUrl = url;
                    break;
                }
            }

            if (string.IsNullOrWhiteSpace(html))
                return new(false,
                    $"Public ASA mod index returned HTTP {(int)lastStatus}.",
                    Array.Empty<AsaModEntry>());

            var ids = Regex.Matches(
                    html,
                    @"(?:https?://arkcodes\.com)?/mods/([0-9]{4,10})/?",
                    RegexOptions.IgnoreCase)
                .Select(m => m.Groups[1].Value)
                .Distinct()
                .Take(Math.Max(pageSize * 3, 50))
                .ToList();

            if (ids.Count == 0)
                return new(false,
                    $"Public ASA mod index loaded but exposed no numeric project IDs. Source: {usedUrl}",
                    Array.Empty<AsaModEntry>());

            var gate = new SemaphoreSlim(8);
            var tasks = ids.Select(async id =>
            {
                await gate.WaitAsync(ct);
                try { return await GetModFromPublicMetadataAsync(id, ct); }
                finally { gate.Release(); }
            }).ToArray();

            var resolved = (await Task.WhenAll(tasks))
                .Where(m => m is not null)
                .Cast<AsaModEntry>()
                .Where(m =>
                    string.IsNullOrWhiteSpace(search) ||
                    m.DisplayName.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                    m.Author.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                    m.Summary.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                    m.PrimaryCategory.Contains(search, StringComparison.OrdinalIgnoreCase))
                .ToList();

            IEnumerable<AsaModEntry> ordered = query.Sort switch
            {
                ModBrowseSort.LastUpdated => query.Descending
                    ? resolved.OrderByDescending(m => m.LastUpdated)
                    : resolved.OrderBy(m => m.LastUpdated),
                ModBrowseSort.Downloads or ModBrowseSort.Popularity => query.Descending
                    ? resolved.OrderByDescending(m => m.Downloads)
                    : resolved.OrderBy(m => m.Downloads),
                ModBrowseSort.Rating => query.Descending
                    ? resolved.OrderByDescending(m => m.Rating ?? 0)
                    : resolved.OrderBy(m => m.Rating ?? 0),
                ModBrowseSort.Size => query.Descending
                    ? resolved.OrderByDescending(m => m.MainFileSizeBytes ?? 0)
                    : resolved.OrderBy(m => m.MainFileSizeBytes ?? 0),
                ModBrowseSort.ReleasedDate => query.Descending
                    ? resolved.OrderByDescending(m => m.LastUpdated)
                    : resolved.OrderBy(m => m.LastUpdated),
                ModBrowseSort.Name => query.Descending
                    ? resolved.OrderByDescending(m => m.DisplayName)
                    : resolved.OrderBy(m => m.DisplayName),
                ModBrowseSort.Author => query.Descending
                    ? resolved.OrderByDescending(m => m.Author)
                    : resolved.OrderBy(m => m.Author),
                _ => resolved
            };

            var result = ordered.Take(pageSize).ToList();
            return new(true,
                $"Loaded {result.Count} matching ASA mods from the public catalogue.",
                result);
        }
        catch (Exception ex)
        {
            return new(false,
                $"Public ASA mod catalogue failed: {ex.Message}",
                Array.Empty<AsaModEntry>());
        }
    }

    private async Task<ModBrowseResponse> SearchPublicCurseForgeAsync(
        ModBrowseQuery query,
        CancellationToken ct)
    {
        try
        {
            var pageSize = Math.Clamp(query.PageSize, 1, 50);
            var sortBy = query.Sort switch
            {
                ModBrowseSort.LastUpdated => "latest+update",
                ModBrowseSort.Downloads => "total+downloads",
                ModBrowseSort.Name => "a-z",
                ModBrowseSort.Popularity => "popularity",
                ModBrowseSort.ReleasedDate => "creation+date",
                _ => "relevancy"
            };

            var url =
                $"https://www.curseforge.com/ark-survival-ascended/search?class=mods&page=1&pageSize={pageSize}&sortBy={sortBy}";

            if (!string.IsNullOrWhiteSpace(query.Search))
                url += "&search=" + Uri.EscapeDataString(query.Search.Trim());

            using var response = await _http.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode)
                return new(false, $"Public catalogue returned HTTP {(int)response.StatusCode}.", Array.Empty<AsaModEntry>());

            var html = await response.Content.ReadAsStringAsync(ct);

            // CurseForge changes its frontend markup often. Do not depend on a
            // specific <a href="..."> representation. Normalize escaped JSON/HTML
            // then discover every ASA mod path in the response.
            var normalizedHtml = html
                .Replace(@"\/", "/")
                .Replace(@"\u002F", "/", StringComparison.OrdinalIgnoreCase)
                .Replace("&quot;", "\"");

            var slugs = Regex.Matches(
                    normalizedHtml,
                    @"/ark-survival-ascended/mods/([a-z0-9][a-z0-9-]{1,120})",
                    RegexOptions.IgnoreCase)
                .Select(m => m.Groups[1].Value)
                .Where(slug =>
                    !string.Equals(slug, "search", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(slug, "files", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(pageSize)
                .ToList();

            if (slugs.Count == 0)
            {
                var title = HtmlMatch(html, @"<title[^>]*>(.*?)</title>");
                return new(false,
                    $"Public catalogue returned HTML but no ASA mod links were recognized. " +
                    $"Page title: '{title}'. Response length: {html.Length:N0} bytes.",
                    Array.Empty<AsaModEntry>());
            }

            var gate = new SemaphoreSlim(6);
            var tasks = slugs.Select(async slug =>
            {
                await gate.WaitAsync(ct);
                try { return await GetModFromCurseForgePageAsync(slug, ct); }
                finally { gate.Release(); }
            }).ToArray();

            var resolved = (await Task.WhenAll(tasks))
                .Where(m => m is not null)
                .Cast<AsaModEntry>()
                .ToList();

            IEnumerable<AsaModEntry> ordered = resolved;
            ordered = query.Sort switch
            {
                ModBrowseSort.LastUpdated => query.Descending
                    ? resolved.OrderByDescending(m => m.LastUpdated)
                    : resolved.OrderBy(m => m.LastUpdated),
                ModBrowseSort.Downloads => query.Descending
                    ? resolved.OrderByDescending(m => m.Downloads)
                    : resolved.OrderBy(m => m.Downloads),
                ModBrowseSort.Rating => query.Descending
                    ? resolved.OrderByDescending(m => m.Rating ?? 0)
                    : resolved.OrderBy(m => m.Rating ?? 0),
                ModBrowseSort.Size => query.Descending
                    ? resolved.OrderByDescending(m => m.MainFileSizeBytes ?? 0)
                    : resolved.OrderBy(m => m.MainFileSizeBytes ?? 0),
                ModBrowseSort.Name => query.Descending
                    ? resolved.OrderByDescending(m => m.DisplayName)
                    : resolved.OrderBy(m => m.DisplayName),
                ModBrowseSort.Author => query.Descending
                    ? resolved.OrderByDescending(m => m.Author)
                    : resolved.OrderBy(m => m.Author),
                _ => resolved
            };

            return new(true,
                $"Loaded {resolved.Count} mods from the public CurseForge catalogue.",
                ordered.ToList());
        }
        catch (Exception ex)
        {
            return new(false, $"Public mod catalogue failed: {ex.Message}", Array.Empty<AsaModEntry>());
        }
    }

    private async Task<AsaModEntry?> GetModFromCurseForgePageAsync(string slug, CancellationToken ct)
    {
        try
        {
            var url = $"https://www.curseforge.com/ark-survival-ascended/mods/{slug}";
            using var response = await _http.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode)
                return null;

            var html = await response.Content.ReadAsStringAsync(ct);
            var text = HtmlToText(html);

            var id = MatchValue(text, @"Project ID\s*([0-9]+)");
            if (string.IsNullOrWhiteSpace(id))
                return null;

            var title = HtmlMatch(html, @"<h1[^>]*>(.*?)</h1>");
            var author = MatchValue(text, @"\bBy\s+([^\r\n]+)");
            var downloadsText = MatchValue(text, @"Downloads\s*([0-9,\.]+[KMB]?)");
            var updatedText = MatchValue(text, @"Updated\s*([^\r\n]+)");
            var category = MatchValue(text, @"Categories?\s*([^\r\n]+)");
            var mainFile = MatchValue(text, @"Main File[\s\S]{0,300}?\b([^\r\n]+\.zip)");
            var platform = text.Contains("Cross-Platform", StringComparison.OrdinalIgnoreCase)
                ? "Cross-Platform"
                : "Unknown";

            return new AsaModEntry
            {
                ModId = id,
                DisplayName = string.IsNullOrWhiteSpace(title) ? slug : title,
                Author = author,
                Summary = ExtractFirstParagraphAfterHeading(html, "Description"),
                Platform = platform,
                Downloads = ParseCompactNumber(downloadsText),
                PrimaryCategory = category,
                LastUpdated = DateTimeOffset.TryParse(updatedText, out var updated) ? updated : null,
                MainFileName = mainFile,
                IsAvailable = true,
                WebsiteUrl = url,
                MetadataStatus = "Loaded from public CurseForge catalogue"
            };
        }
        catch
        {
            return null;
        }
    }

    private static string HtmlToText(string html)
    {
        var withoutScripts = Regex.Replace(html, @"<script[\s\S]*?</script>|<style[\s\S]*?</style>", " ",
            RegexOptions.IgnoreCase);
        var text = Regex.Replace(withoutScripts, "<[^>]+>", "\n");
        text = WebUtility.HtmlDecode(text);
        text = Regex.Replace(text, @"[ \t]+", " ");
        text = Regex.Replace(text, @"\n\s*\n+", "\n");
        return text.Trim();
    }

    private static string HtmlMatch(string html, string pattern)
    {
        var match = Regex.Match(html, pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline);
        return match.Success
            ? WebUtility.HtmlDecode(Regex.Replace(match.Groups[1].Value, "<[^>]+>", " ")).Trim()
            : string.Empty;
    }

    private static string MatchValue(string text, string pattern)
    {
        var match = Regex.Match(text, pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline);
        return match.Success ? match.Groups[1].Value.Trim() : string.Empty;
    }

    private static string ExtractFirstParagraphAfterHeading(string html, string heading)
    {
        var headingIndex = html.IndexOf(heading, StringComparison.OrdinalIgnoreCase);
        if (headingIndex < 0)
            return string.Empty;

        var slice = html[headingIndex..Math.Min(html.Length, headingIndex + 12000)];
        var match = Regex.Match(slice, @"<p[^>]*>(.*?)</p>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        return match.Success
            ? WebUtility.HtmlDecode(Regex.Replace(match.Groups[1].Value, "<[^>]+>", " ")).Trim()
            : string.Empty;
    }

    private static long ParseCompactNumber(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return 0;

        var cleaned = value.Replace(",", "").Trim();
        var multiplier = 1d;

        if (cleaned.EndsWith("K", StringComparison.OrdinalIgnoreCase))
        {
            multiplier = 1_000d;
            cleaned = cleaned[..^1];
        }
        else if (cleaned.EndsWith("M", StringComparison.OrdinalIgnoreCase))
        {
            multiplier = 1_000_000d;
            cleaned = cleaned[..^1];
        }
        else if (cleaned.EndsWith("B", StringComparison.OrdinalIgnoreCase))
        {
            multiplier = 1_000_000_000d;
            cleaned = cleaned[..^1];
        }

        return double.TryParse(cleaned, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var number)
            ? (long)(number * multiplier)
            : 0;
    }

    private static long? ParseSizeBytes(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var match = Regex.Match(value, @"([0-9\.]+)\s*(B|KB|MB|GB|TB)", RegexOptions.IgnoreCase);
        if (!match.Success ||
            !double.TryParse(match.Groups[1].Value, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var number))
            return null;

        var power = match.Groups[2].Value.ToUpperInvariant() switch
        {
            "KB" => 1,
            "MB" => 2,
            "GB" => 3,
            "TB" => 4,
            _ => 0
        };

        return (long)(number * Math.Pow(1024, power));
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
