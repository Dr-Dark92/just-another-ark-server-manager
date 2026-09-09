using System.Net;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using JAASM.App.Models;

namespace JAASM.App.Services;

public sealed record ModDownloadProgress(
    string ModId,
    long BytesReceived,
    long? TotalBytes,
    double? Percent,
    string Status);

public sealed record ModDownloadResult(
    bool Success,
    string Message,
    string? FilePath = null,
    int? FileId = null);

public sealed class ModDownloadService
{
    private readonly HttpClient _http;

    public ModDownloadService()
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.All
        };

        _http = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromMinutes(30)
        };

        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) " +
            "AppleWebKit/537.36 (KHTML, like Gecko) Chrome/152.0.0.0 Safari/537.36");
        _http.DefaultRequestHeaders.Accept.ParseAdd("*/*");
        _http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
    }

    public async Task<ModDownloadResult> DownloadLatestWindowsServerPackageAsync(
        AsaModEntry mod,
        string destinationRoot,
        IProgress<ModDownloadProgress>? progress = null,
        CancellationToken ct = default)
    {
        try
        {
            progress?.Report(new(
                mod.ModId, 0, null, null, "Resolving CurseForge project..."));

            var projectUrl = await ResolveCurseForgeProjectUrlAsync(mod, ct);
            if (projectUrl is null)
            {
                return new(false,
                    $"Could not resolve a CurseForge project page for mod {mod.ModId}.");
            }

            progress?.Report(new(
                mod.ModId, 0, null, null, "Resolving latest Windows server file..."));

            var file = await ResolveLatestServerFileAsync(projectUrl, mod.ModId, ct);
            if (file is null)
            {
                return new(false,
                    $"Could not resolve the latest Windows server package for mod {mod.ModId}.");
            }

            var modDirectory = Path.Combine(destinationRoot, mod.ModId);
            Directory.CreateDirectory(modDirectory);

            var safeName = SanitizeFileName(
                string.IsNullOrWhiteSpace(file.Value.FileName)
                    ? $"{mod.ModId}-{file.Value.FileId}.zip"
                    : file.Value.FileName);

            var finalPath = Path.Combine(modDirectory, safeName);
            var tempPath = finalPath + ".part";

            if (File.Exists(tempPath))
                File.Delete(tempPath);

            progress?.Report(new(
                mod.ModId, 0, null, 0, $"Starting download: {safeName}"));

            using var request = new HttpRequestMessage(HttpMethod.Get, file.Value.DownloadUrl);
            using var response = await _http.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                ct);

            if (!response.IsSuccessStatusCode)
            {
                return new(false,
                    $"CurseForge download returned HTTP {(int)response.StatusCode}.");
            }

            var total = response.Content.Headers.ContentLength;

            var responseName = TryGetResponseFileName(response.Content.Headers.ContentDisposition);
            if (!string.IsNullOrWhiteSpace(responseName))
            {
                safeName = SanitizeFileName(responseName);
                finalPath = Path.Combine(modDirectory, safeName);
                tempPath = finalPath + ".part";
            }

            await using var input = await response.Content.ReadAsStreamAsync(ct);
            await using var output = new FileStream(
                tempPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                1024 * 128,
                useAsync: true);

            var buffer = new byte[1024 * 128];
            long received = 0;
            var lastReport = DateTimeOffset.MinValue;

            while (true)
            {
                var read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), ct);
                if (read <= 0)
                    break;

                await output.WriteAsync(buffer.AsMemory(0, read), ct);
                received += read;

                var now = DateTimeOffset.UtcNow;
                if (now - lastReport >= TimeSpan.FromMilliseconds(120))
                {
                    var percent = total is > 0
                        ? Math.Clamp(received * 100d / total.Value, 0d, 100d)
                        : null;

                    progress?.Report(new(
                        mod.ModId,
                        received,
                        total,
                        percent,
                        total is > 0
                            ? $"Downloading {FormatBytes(received)} / {FormatBytes(total.Value)}"
                            : $"Downloading {FormatBytes(received)}"));

                    lastReport = now;
                }
            }

            await output.FlushAsync(ct);

            if (File.Exists(finalPath))
                File.Delete(finalPath);

            File.Move(tempPath, finalPath);

            progress?.Report(new(
                mod.ModId,
                received,
                total ?? received,
                100,
                $"Download complete: {Path.GetFileName(finalPath)}"));

            return new(
                true,
                $"Downloaded {mod.DisplayName}.",
                finalPath,
                file.Value.FileId);
        }
        catch (OperationCanceledException)
        {
            return new(false, $"Download cancelled for mod {mod.ModId}.");
        }
        catch (Exception ex)
        {
            return new(false, $"Mod download failed: {ex.Message}");
        }
    }

    private async Task<string?> ResolveCurseForgeProjectUrlAsync(
        AsaModEntry mod,
        CancellationToken ct)
    {
        if (TryNormalizeCurseForgeProjectUrl(mod.WebsiteUrl, out var direct))
            return direct;

        // ArkCodes often includes a CurseForge link in the rendered/project HTML.
        var arkUrl = !string.IsNullOrWhiteSpace(mod.WebsiteUrl) &&
                     mod.WebsiteUrl.Contains("arkcodes.com", StringComparison.OrdinalIgnoreCase)
            ? mod.WebsiteUrl
            : $"https://arkcodes.com/mods/{mod.ModId}/";

        try
        {
            using var arkResponse = await _http.GetAsync(arkUrl, ct);
            if (arkResponse.IsSuccessStatusCode)
            {
                var html = await arkResponse.Content.ReadAsStringAsync(ct);
                var match = Regex.Match(
                    html,
                    @"https?://(?:www\.)?curseforge\.com/ark-survival-ascended/mods/([a-z0-9-]+)",
                    RegexOptions.IgnoreCase);

                if (match.Success)
                    return $"https://www.curseforge.com/ark-survival-ascended/mods/{match.Groups[1].Value}";
            }
        }
        catch
        {
            // Fall through to CurseForge search-by-ID.
        }

        // Final public fallback: search CurseForge with the numeric project ID,
        // then verify candidates by the Project ID printed on each project page.
        var searchUrl =
            "https://www.curseforge.com/ark-survival-ascended/search?class=mods&search=" +
            Uri.EscapeDataString(mod.ModId);

        using var searchResponse = await _http.GetAsync(searchUrl, ct);
        if (!searchResponse.IsSuccessStatusCode)
            return null;

        var searchHtml = await searchResponse.Content.ReadAsStringAsync(ct);

        var candidates = Regex.Matches(
                searchHtml,
                "href=[\\\"\'](/ark-survival-ascended/mods/[a-z0-9-]+)[\\\"\']",
                RegexOptions.IgnoreCase)
            .Select(m => "https://www.curseforge.com" + WebUtility.HtmlDecode(m.Groups[1].Value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(12)
            .ToList();

        foreach (var candidate in candidates)
        {
            try
            {
                using var response = await _http.GetAsync(candidate, ct);
                if (!response.IsSuccessStatusCode)
                    continue;

                var html = await response.Content.ReadAsStringAsync(ct);
                var text = HtmlToText(html);
                var id = Regex.Match(
                    text,
                    @"Project\s*ID\s*:?\s*(\d{4,10})",
                    RegexOptions.IgnoreCase);

                if (id.Success && id.Groups[1].Value == mod.ModId)
                    return candidate;
            }
            catch
            {
                // Try next candidate.
            }
        }

        return null;
    }

    private async Task<(int FileId, string FileName, string DownloadUrl)?>
        ResolveLatestServerFileAsync(
            string projectUrl,
            string modId,
            CancellationToken ct)
    {
        using var response = await _http.GetAsync(projectUrl, ct);
        if (!response.IsSuccessStatusCode)
            return null;

        var html = await response.Content.ReadAsStringAsync(ct);
        var text = HtmlToText(html);

        var projectId = Regex.Match(
            text,
            @"Project\s*ID\s*:?\s*(\d{4,10})",
            RegexOptions.IgnoreCase);

        if (!projectId.Success || projectId.Groups[1].Value != modId)
            return null;

        // The project-level Download button points to the current main file.
        // For ASA project pages the Main File is normally the Windows-server package.
        var downloadMatches = Regex.Matches(
            html,
            "href=[\\\"\']([^ \\\"\']*/ark-survival-ascended/mods/[a-z0-9-]+/download/(\\d+))[\\\"\']",
            RegexOptions.IgnoreCase);

        Match? chosen = downloadMatches.Cast<Match>().FirstOrDefault();
        if (chosen is null)
        {
            chosen = Regex.Matches(
                    html,
                    "href=[\\\"\']([^ \\\"\']*/download/(\\d+))[\\\"\']",
                    RegexOptions.IgnoreCase)
                .Cast<Match>()
                .FirstOrDefault();
        }

        if (chosen is null || !int.TryParse(chosen.Groups[2].Value, out var fileId))
            return null;

        var downloadUrl = WebUtility.HtmlDecode(chosen.Groups[1].Value);
        if (downloadUrl.StartsWith('/'))
            downloadUrl = "https://www.curseforge.com" + downloadUrl;
        else if (!Uri.TryCreate(downloadUrl, UriKind.Absolute, out _))
            downloadUrl = projectUrl.TrimEnd('/') + "/" + downloadUrl.TrimStart('/');

        var mainFile = Regex.Match(
            text,
            @"Main\s*File[\s\S]{0,500}?([^\r\n]+windowsserver[^\r\n]+\.zip)",
            RegexOptions.IgnoreCase);

        var fileName = mainFile.Success
            ? mainFile.Groups[1].Value.Trim()
            : $"{modId}-{fileId}-windowsserver.zip";

        return (fileId, fileName, downloadUrl);
    }

    private static bool TryNormalizeCurseForgeProjectUrl(
        string? value,
        out string? normalized)
    {
        normalized = null;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
            return false;

        if (!uri.Host.EndsWith("curseforge.com", StringComparison.OrdinalIgnoreCase))
            return false;

        var match = Regex.Match(
            uri.AbsolutePath,
            @"^/ark-survival-ascended/mods/([a-z0-9-]+)",
            RegexOptions.IgnoreCase);

        if (!match.Success)
            return false;

        normalized =
            $"https://www.curseforge.com/ark-survival-ascended/mods/{match.Groups[1].Value}";
        return true;
    }

    private static string? TryGetResponseFileName(ContentDispositionHeaderValue? disposition)
    {
        var value = disposition?.FileNameStar ?? disposition?.FileName;
        return string.IsNullOrWhiteSpace(value)
            ? null
            : value.Trim().Trim('"');
    }

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = value.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        return new string(chars).Trim();
    }

    private static string HtmlToText(string html)
    {
        var text = Regex.Replace(
            html,
            @"<script[\s\S]*?</script>|<style[\s\S]*?</style>",
            " ",
            RegexOptions.IgnoreCase);

        text = Regex.Replace(text, "<[^>]+>", "\n");
        text = WebUtility.HtmlDecode(text);
        text = Regex.Replace(text, @"[ \t]+", " ");
        text = Regex.Replace(text, @"\n\s*\n+", "\n");
        return text.Trim();
    }

    private static string FormatBytes(long value)
    {
        double size = value;
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        var unit = 0;

        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return $"{size:0.##} {units[unit]}";
    }
}
