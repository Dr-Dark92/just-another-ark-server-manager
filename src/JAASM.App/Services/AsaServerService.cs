namespace JAASM.App.Services;

public sealed record AsaValidationResult(bool Success, string Message, string? ExecutablePath);

public sealed class AsaServerService
{
    public const int AppId = 2430930;
    public const string WindowsExecutableRelativePath =
        "ShooterGame\\Binaries\\Win64\\ArkAscendedServer.exe";

    private readonly SteamCmdService _steamCmd;

    public AsaServerService(SteamCmdService steamCmd)
    {
        _steamCmd = steamCmd;
    }

    public string GetExpectedExecutablePath(string installDirectory) =>
        Path.Combine(installDirectory, WindowsExecutableRelativePath);

    public AsaValidationResult ValidateInstallation(string installDirectory)
    {
        if (string.IsNullOrWhiteSpace(installDirectory))
            return new(false, "ASA installation directory is not configured.", null);

        var directory = Path.GetFullPath(installDirectory);

        if (!Directory.Exists(directory))
            return new(false, $"ASA installation directory does not exist: {directory}", null);

        if (!OperatingSystem.IsWindows())
        {
            return new(false,
                "Native ASA Dedicated Server runtime is currently Windows-targeted. Linux compatibility/runtime support will be handled separately.",
                null);
        }

        var executable = GetExpectedExecutablePath(directory);

        if (!File.Exists(executable))
            return new(false, $"ArkAscendedServer.exe was not found at {executable}", executable);

        return new(true, $"ASA Dedicated Server installation validated: {executable}", executable);
    }

    public async Task<AsaValidationResult> InstallOrUpdateAsync(
        string steamCmdExecutable,
        string installDirectory,
        IProgress<string>? console = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(steamCmdExecutable) || !File.Exists(steamCmdExecutable))
            return new(false, "SteamCMD must be configured and validated first.", null);

        if (string.IsNullOrWhiteSpace(installDirectory))
            return new(false, "Select an ASA server installation directory first.", null);

        installDirectory = Path.GetFullPath(installDirectory);
        Directory.CreateDirectory(installDirectory);

        console?.Report($"[ASA] App ID: {AppId}");
        console?.Report($"[ASA] Install directory: {installDirectory}");

        var escapedDirectory = installDirectory.Replace(""", "\\"");
        var arguments =
            $"+force_install_dir \"{escapedDirectory}\" +login anonymous +app_update {AppId} validate +quit";

        var result = await _steamCmd.ExecuteAsync(
            steamCmdExecutable,
            arguments,
            console,
            ct);

        if (!result.Success)
        {
            var detail = result.Output.Trim();
            if (detail.Length > 1600)
                detail = detail[^1600..];

            return new(false,
                $"SteamCMD failed to install/update ASA. Exit code {result.ExitCode}. {detail}",
                null);
        }

        return ValidateInstallation(installDirectory);
    }
}
