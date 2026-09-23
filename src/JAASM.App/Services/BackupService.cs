using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using JAASM.App.Models;

namespace JAASM.App.Services;

public sealed record BackupResult(bool Success, string Message, string? ArchivePath = null, string? Sha256 = null);
public sealed record RestoreReview(bool Success, string Message, string ArchivePath, string ServerName, string Map, string ProfileId, DateTimeOffset? CreatedUtc, int FileCount, long TotalBytes, IReadOnlyList<string> Files);

public sealed class BackupService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public async Task<BackupResult> CreateAsync(string asaInstallDirectory, AsaServerProfile profile, string destination, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(asaInstallDirectory) || !Directory.Exists(asaInstallDirectory))
            return new(false, "ASA installation directory does not exist.");
        if (string.IsNullOrWhiteSpace(destination))
            return new(false, "Select a backup destination.");

        Directory.CreateDirectory(destination);
        var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss");
        var safeName = string.Concat(profile.ServerName.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
        var archive = Path.Combine(destination, $"{safeName}-{stamp}.jaasm-backup.zip");
        var staging = Path.Combine(Path.GetTempPath(), $"jaasm-backup-{profile.Id}-{Guid.NewGuid():N}");

        try
        {
            Directory.CreateDirectory(staging);
            var files = new List<string>();

            var savedRoot = Path.Combine(asaInstallDirectory, "ShooterGame", "Saved");
            foreach (var relative in new[] {
                Path.Combine("SavedArks"),
                Path.Combine("Config", "WindowsServer", "Game.ini"),
                Path.Combine("Config", "WindowsServer", "GameUserSettings.ini")
            })
            {
                var source = Path.Combine(savedRoot, relative);
                if (Directory.Exists(source)) CopyDirectory(source, Path.Combine(staging, "server", relative), files, staging);
                else if (File.Exists(source)) CopyFile(source, Path.Combine(staging, "server", relative), files, staging);
            }

            await File.WriteAllTextAsync(Path.Combine(staging, "profile.json"), JsonSerializer.Serialize(profile, JsonOptions), ct);
            files.Add("profile.json");

            var entries = new List<object>();
            foreach (var relative in files.OrderBy(x => x))
            {
                var full = Path.Combine(staging, relative.Replace('/', Path.DirectorySeparatorChar));
                entries.Add(new { path = relative, size = new FileInfo(full).Length, sha256 = await HashAsync(full, ct) });
            }

            var manifest = new {
                format = "JAASM-BACKUP", version = 1, createdUtc = DateTimeOffset.UtcNow,
                profileId = profile.Id, serverName = profile.ServerName, map = profile.Map,
                asaAppId = 2430930, sourcePlatform = Environment.OSVersion.Platform.ToString(), files = entries
            };
            await File.WriteAllTextAsync(Path.Combine(staging, "manifest.json"), JsonSerializer.Serialize(manifest, JsonOptions), ct);
            if (File.Exists(archive)) File.Delete(archive);
            ZipFile.CreateFromDirectory(staging, archive, CompressionLevel.Optimal, false);
            var archiveHash = await HashAsync(archive, ct);
            await File.WriteAllTextAsync(archive + ".sha256", $"{archiveHash}  {Path.GetFileName(archive)}\n", ct);
            return new(true, $"Backup created and SHA-256 verified: {Path.GetFileName(archive)}", archive, archiveHash);
        }
        catch (Exception ex) { return new(false, $"Backup failed: {ex.Message}"); }
        finally { try { if (Directory.Exists(staging)) Directory.Delete(staging, true); } catch { } }
    }

    public async Task<BackupResult> VerifyAsync(string archive, CancellationToken ct = default)
    {
        try {
            if (!File.Exists(archive)) return new(false, "Backup archive does not exist.");
            var hash = await HashAsync(archive, ct);
            var sidecar = archive + ".sha256";
            if (File.Exists(sidecar)) {
                var expected = (await File.ReadAllTextAsync(sidecar, ct)).Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
                if (!hash.Equals(expected, StringComparison.OrdinalIgnoreCase)) return new(false, "Backup SHA-256 verification FAILED.", archive, hash);
            }
            using var zip = ZipFile.OpenRead(archive);
            if (zip.GetEntry("manifest.json") is null || zip.GetEntry("profile.json") is null)
                return new(false, "Backup is missing manifest.json or profile.json.", archive, hash);
            return new(true, "Backup archive verified successfully.", archive, hash);
        } catch (Exception ex) { return new(false, $"Verification failed: {ex.Message}"); }
    }

    public async Task<RestoreReview> ReviewAsync(string archive, CancellationToken ct = default)
    {
        var verify = await VerifyAsync(archive, ct);
        if (!verify.Success) return new(false, verify.Message, archive, "", "", "", null, 0, 0, Array.Empty<string>());
        try {
            using var zip = ZipFile.OpenRead(archive);
            var manifestEntry = zip.GetEntry("manifest.json")!;
            await using var stream = manifestEntry.Open();
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var root = document.RootElement;
            if (root.GetProperty("format").GetString() != "JAASM-BACKUP" || root.GetProperty("version").GetInt32() != 1)
                return new(false, "Unsupported or invalid JAASM backup format.", archive, "", "", "", null, 0, 0, Array.Empty<string>());
            var names = new List<string>(); long bytes = 0;
            foreach (var entry in root.GetProperty("files").EnumerateArray()) {
                names.Add(entry.GetProperty("path").GetString() ?? "");
                bytes += entry.TryGetProperty("size", out var size) ? size.GetInt64() : 0;
            }
            DateTimeOffset? created = root.TryGetProperty("createdUtc", out var createdEl) && createdEl.TryGetDateTimeOffset(out var dt) ? dt : null;
            return new(true, "Backup verified and ready for review.", archive,
                root.GetProperty("serverName").GetString() ?? "",
                root.GetProperty("map").GetString() ?? "",
                root.GetProperty("profileId").GetString() ?? "",
                created, names.Count, bytes, names);
        } catch (Exception ex) {
            return new(false, $"Review failed: {ex.Message}", archive, "", "", "", null, 0, 0, Array.Empty<string>());
        }
    }

    public async Task<BackupResult> RestoreAsync(string archive, string asaInstallDirectory, string rollbackDestination, CancellationToken ct = default)
    {
        var verify = await VerifyAsync(archive, ct);
        if (!verify.Success) return verify;
        if (string.IsNullOrWhiteSpace(asaInstallDirectory) || !Directory.Exists(asaInstallDirectory))
            return new(false, "ASA installation directory does not exist.");

        var staging = Path.Combine(Path.GetTempPath(), $"jaasm-restore-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(staging);
            ZipFile.ExtractToDirectory(archive, staging);

            var manifestPath = Path.Combine(staging, "manifest.json");
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath, ct));
            var root = document.RootElement;
            if (root.GetProperty("format").GetString() != "JAASM-BACKUP" || root.GetProperty("version").GetInt32() != 1)
                return new(false, "Unsupported or invalid JAASM backup format.");

            foreach (var entry in root.GetProperty("files").EnumerateArray())
            {
                var relative = entry.GetProperty("path").GetString() ?? string.Empty;
                if (relative.Contains("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
                    return new(false, $"Unsafe path in backup manifest: {relative}");
                var full = Path.GetFullPath(Path.Combine(staging, relative.Replace('/', Path.DirectorySeparatorChar)));
                if (!full.StartsWith(Path.GetFullPath(staging) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                    return new(false, $"Unsafe path in backup manifest: {relative}");
                if (!File.Exists(full)) return new(false, $"Backup file is missing: {relative}");
                var expected = entry.GetProperty("sha256").GetString() ?? string.Empty;
                var actual = await HashAsync(full, ct);
                if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
                    return new(false, $"Backup content verification failed: {relative}");
            }

            var serverStage = Path.Combine(staging, "server");
            if (!Directory.Exists(serverStage)) return new(false, "Backup contains no server data.");

            Directory.CreateDirectory(rollbackDestination);
            var rollback = Path.Combine(rollbackDestination, $"pre-restore-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.jaasm-rollback.zip");
            var liveSaved = Path.Combine(asaInstallDirectory, "ShooterGame", "Saved");
            if (Directory.Exists(liveSaved))
                ZipFile.CreateFromDirectory(liveSaved, rollback, CompressionLevel.Optimal, false);

            foreach (var relative in new[] { "SavedArks", Path.Combine("Config", "WindowsServer", "Game.ini"), Path.Combine("Config", "WindowsServer", "GameUserSettings.ini") })
            {
                var source = Path.Combine(serverStage, relative);
                var target = Path.Combine(liveSaved, relative);
                if (Directory.Exists(source)) {
                    if (Directory.Exists(target)) Directory.Delete(target, true);
                    CopyDirectoryRaw(source, target);
                } else if (File.Exists(source)) {
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(source, target, true);
                }
            }

            return new(true, $"Restore completed successfully. Emergency rollback: {rollback}", archive, verify.Sha256);
        }
        catch (Exception ex) { return new(false, $"Restore failed: {ex.Message}"); }
        finally { try { if (Directory.Exists(staging)) Directory.Delete(staging, true); } catch { } }
    }

    private static void CopyDirectoryRaw(string source, string destination)
    {
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories)) {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, true);
        }
    }

    private static void CopyDirectory(string source, string destination, List<string> files, string staging) {
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories)) {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            CopyFile(file, target, files, staging);
        }
    }
    private static void CopyFile(string source, string destination, List<string> files, string staging) {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!); File.Copy(source, destination, true);
        files.Add(Path.GetRelativePath(staging, destination).Replace('\\', '/'));
    }
    private static async Task<string> HashAsync(string path, CancellationToken ct) {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
