using Microsoft.Win32;
using System.Diagnostics;
using System.Runtime.Versioning;

namespace JAASM.App.Services;

public sealed class StartupService
{
    private const string AppName = "JAASM";

    public string PlatformDescription =>
        OperatingSystem.IsWindows() ? "Windows current-user startup" :
        OperatingSystem.IsLinux() ? "Linux systemd --user service" :
        "Unsupported platform";

    public async Task<(bool Success, string Message)> SetEnabledAsync(bool enabled)
    {
        try
        {
            if (OperatingSystem.IsWindows())
                return SetWindowsStartup(enabled);
            if (OperatingSystem.IsLinux())
                return await SetLinuxStartupAsync(enabled);

            return (false, "Startup integration is not supported on this operating system.");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    [SupportedOSPlatform("windows")]
    private static (bool Success, string Message) SetWindowsStartup(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        if (key is null) return (false, "Could not open the current-user Windows startup registry key.");

        if (enabled)
            key.SetValue(AppName, Quote(Environment.ProcessPath ?? throw new InvalidOperationException("JAASM executable path is unavailable.")));
        else
            key.DeleteValue(AppName, false);

        return (true, enabled ? "JAASM will start when this Windows user signs in." : "JAASM Windows startup entry removed.");
    }

    private static async Task<(bool Success, string Message)> SetLinuxStartupAsync(bool enabled)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var directory = Path.Combine(home, ".config", "systemd", "user");
        var servicePath = Path.Combine(directory, "jaasm.service");

        if (enabled)
        {
            Directory.CreateDirectory(directory);
            var executable = Environment.ProcessPath ?? throw new InvalidOperationException("JAASM executable path is unavailable.");
            var unit = $"""
[Unit]
Description=Just Another ARK Server Manager
After=graphical-session.target network-online.target
Wants=network-online.target

[Service]
Type=simple
ExecStart={EscapeSystemd(executable)}
Restart=on-failure
RestartSec=5

[Install]
WantedBy=default.target
""";
            await File.WriteAllTextAsync(servicePath, unit);
            var reload = await RunSystemctlAsync("daemon-reload");
            if (!reload.Success) return reload;
            var enable = await RunSystemctlAsync("enable", "--now", "jaasm.service");
            return enable.Success ? (true, "JAASM systemd user service installed, enabled, and started.") : enable;
        }

        await RunSystemctlAsync("disable", "--now", "jaasm.service");
        if (File.Exists(servicePath)) File.Delete(servicePath);
        await RunSystemctlAsync("daemon-reload");
        return (true, "JAASM systemd user service disabled and removed.");
    }

    private static async Task<(bool Success, string Message)> RunSystemctlAsync(params string[] args)
    {
        var psi = new ProcessStartInfo { FileName = "systemctl", UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        psi.ArgumentList.Add("--user");
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        using var process = Process.Start(psi);
        if (process is null) return (false, "Could not start systemctl.");
        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return process.ExitCode == 0
            ? (true, string.IsNullOrWhiteSpace(stdout) ? "systemctl completed successfully." : stdout.Trim())
            : (false, string.IsNullOrWhiteSpace(stderr) ? $"systemctl exited with code {process.ExitCode}." : stderr.Trim());
    }

    private static string Quote(string value) => $"\"{value.Replace("\"", "\\\"")}\"";
    private static string EscapeSystemd(string value) => value.Replace("%", "%%").Replace(" ", "\\x20");
}
