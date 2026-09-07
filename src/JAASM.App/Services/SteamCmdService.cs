using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;

namespace JAASM.App.Services;

public sealed record SteamCmdValidationResult(bool Success, int? ExitCode, string Message);

public sealed class SteamCmdService
{
    public const string WindowsDownloadUrl =
        "https://steamcdn-a.akamaihd.net/client/installer/steamcmd.zip";
    public const string LinuxDownloadUrl =
        "https://steamcdn-a.akamaihd.net/client/installer/steamcmd_linux.tar.gz";

    private readonly HttpClient _http = new();

    public string ExecutableName =>
        OperatingSystem.IsWindows() ? "steamcmd.exe" :
        OperatingSystem.IsLinux() ? "steamcmd.sh" :
        throw new PlatformNotSupportedException("JAASM Phase 1 supports Windows and Linux.");

    public string? ResolveExecutable(string selectedPath)
    {
        if (File.Exists(selectedPath) &&
            string.Equals(Path.GetFileName(selectedPath), ExecutableName, StringComparison.OrdinalIgnoreCase))
            return Path.GetFullPath(selectedPath);

        var candidate = Path.Combine(selectedPath, ExecutableName);
        return File.Exists(candidate) ? Path.GetFullPath(candidate) : null;
    }

    public async Task<SteamCmdValidationResult> ValidateDetailedAsync(
        string executablePath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(executablePath))
            return new(false, null, $"SteamCMD executable does not exist: {executablePath}");

        var psi = new ProcessStartInfo
        {
            FileName = executablePath,
            Arguments = "+quit",
            WorkingDirectory = Path.GetDirectoryName(executablePath)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        try
        {
            using var process = Process.Start(psi);
            if (process is null)
                return new(false, null, "The operating system could not start SteamCMD.");

            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

            await process.WaitForExitAsync(cancellationToken);
            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            if (process.ExitCode == 0)
                return new(true, 0, "SteamCMD started and exited normally.");

            var detail = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
            detail = detail.Trim();
            if (detail.Length > 800)
                detail = detail[^800..];

            return new(false, process.ExitCode,
                $"SteamCMD exited with code {process.ExitCode}. {detail}");
        }
        catch (Exception ex)
        {
            return new(false, null, $"Could not execute SteamCMD: {ex.Message}");
        }
    }

    public async Task<bool> ValidateAsync(string executablePath, CancellationToken cancellationToken = default) =>
        (await ValidateDetailedAsync(executablePath, cancellationToken)).Success;

    public async Task<string> InstallAsync(
        string installDirectory,
        IProgress<double>? progress = null,
        IProgress<string>? status = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(installDirectory))
            throw new ArgumentException("A SteamCMD installation directory must be selected.", nameof(installDirectory));

        installDirectory = Path.GetFullPath(installDirectory);
        Directory.CreateDirectory(installDirectory);

        var url = OperatingSystem.IsWindows() ? WindowsDownloadUrl :
                  OperatingSystem.IsLinux() ? LinuxDownloadUrl :
                  throw new PlatformNotSupportedException();

        var archivePath = Path.Combine(Path.GetTempPath(),
            OperatingSystem.IsWindows() ? "jaasm-steamcmd.zip" : "jaasm-steamcmd.tar.gz");

        status?.Report($"Downloading SteamCMD to temporary archive...");
        status?.Report($"Target directory: {installDirectory}");

        if (File.Exists(archivePath))
            File.Delete(archivePath);

        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        status?.Report($"HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
        response.EnsureSuccessStatusCode();

        var length = response.Content.Headers.ContentLength;
        long total = 0;

        await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
        await using (var output = File.Create(archivePath))
        {
            var buffer = new byte[81920];
            int read;
            while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                total += read;

                if (length is > 0)
                    progress?.Report((double)total / length.Value);
            }

            await output.FlushAsync(cancellationToken);
        }

        if (!File.Exists(archivePath))
            throw new IOException($"Download completed but archive was not created: {archivePath}");

        var actualSize = new FileInfo(archivePath).Length;
        status?.Report($"Downloaded {actualSize:N0} bytes.");

        if (actualSize <= 0)
            throw new IOException("SteamCMD download produced an empty archive.");

        if (length is > 0 && actualSize != length.Value)
            throw new IOException($"Incomplete SteamCMD download. Expected {length.Value:N0} bytes, got {actualSize:N0}.");

        status?.Report($"Extracting directly to {installDirectory}");

        if (OperatingSystem.IsWindows())
        {
            ZipFile.ExtractToDirectory(archivePath, installDirectory, overwriteFiles: true);
        }
        else
        {
            await using var compressed = File.OpenRead(archivePath);
            await using var gzip = new GZipStream(compressed, CompressionMode.Decompress);
            TarFile.ExtractToDirectory(gzip, installDirectory, overwriteFiles: true);

            var script = Path.Combine(installDirectory, ExecutableName);
            if (File.Exists(script))
            {
                File.SetUnixFileMode(script,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                    UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }
        }

        var executable = Path.Combine(installDirectory, ExecutableName);

        if (!File.Exists(executable))
            throw new FileNotFoundException(
                $"Archive extraction completed, but {ExecutableName} was not found in {installDirectory}. " +
                $"The downloaded archive was preserved at {archivePath} for inspection.");

        status?.Report($"SteamCMD executable found: {executable}");
        status?.Report("Starting SteamCMD validation...");

        var validation = await ValidateDetailedAsync(executable, cancellationToken);
        if (!validation.Success)
            throw new InvalidOperationException(
                $"SteamCMD exists at '{executable}', but validation failed. {validation.Message}. " +
                $"The archive remains at {archivePath}.");

        status?.Report("SteamCMD validation passed.");

        File.Delete(archivePath);
        return executable;
    }
}
