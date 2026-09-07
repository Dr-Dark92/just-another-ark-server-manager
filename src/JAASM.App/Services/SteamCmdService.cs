using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;

namespace JAASM.App.Services;

public sealed record SteamCmdValidationResult(bool Success, int? ExitCode, string Message);

public sealed class SteamCmdService
{
    public const string WindowsDownloadUrl = "https://steamcdn-a.akamaihd.net/client/installer/steamcmd.zip";
    public const string LinuxDownloadUrl = "https://steamcdn-a.akamaihd.net/client/installer/steamcmd_linux.tar.gz";
    private readonly HttpClient _http = new();
    public string ExecutableName => OperatingSystem.IsWindows() ? "steamcmd.exe" : OperatingSystem.IsLinux() ? "steamcmd.sh" : throw new PlatformNotSupportedException();

    public string? ResolveExecutable(string selectedPath)
    {
        if (File.Exists(selectedPath) && string.Equals(Path.GetFileName(selectedPath), ExecutableName, StringComparison.OrdinalIgnoreCase))
            return Path.GetFullPath(selectedPath);
        var candidate = Path.Combine(selectedPath, ExecutableName);
        return File.Exists(candidate) ? Path.GetFullPath(candidate) : null;
    }

    public Task<SteamCmdValidationResult> ValidateDetailedAsync(string executablePath, IProgress<string>? console = null, CancellationToken ct = default) =>
        RunSteamCmdAsync(executablePath, "+login anonymous +quit", console, true, ct);

    private async Task<SteamCmdValidationResult> RunSteamCmdAsync(string executablePath, string arguments, IProgress<string>? console, bool retryBootstrap, CancellationToken ct)
    {
        if (!File.Exists(executablePath)) return new(false, null, $"SteamCMD executable does not exist: {executablePath}");
        console?.Report($"> {Path.GetFileName(executablePath)} {arguments}");
        var psi = new ProcessStartInfo { FileName=executablePath, Arguments=arguments, WorkingDirectory=Path.GetDirectoryName(executablePath)!, UseShellExecute=false, CreateNoWindow=true, RedirectStandardOutput=true, RedirectStandardError=true };
        try
        {
            using var p=Process.Start(psi);
            if(p is null) return new(false,null,"Could not start SteamCMD.");
            var lines=new List<string>();
            async Task PumpAsync(StreamReader reader)
            {
                while(await reader.ReadLineAsync(ct) is { } line) { lines.Add(line); console?.Report(line); }
            }
            var o=PumpAsync(p.StandardOutput); var e=PumpAsync(p.StandardError);
            await p.WaitForExitAsync(ct); await Task.WhenAll(o,e);
            var combined=string.Join(Environment.NewLine,lines);
            console?.Report($"[SteamCMD exited: {p.ExitCode}]");
            if(p.ExitCode==0) return new(true,0,"SteamCMD anonymous login validation passed.");
            if(retryBootstrap && p.ExitCode==7 && combined.Contains("Update complete",StringComparison.OrdinalIgnoreCase))
            {
                console?.Report("[SteamCMD self-update completed; re-validating updated binary...]");
                await Task.Delay(1500,ct);
                return await RunSteamCmdAsync(executablePath,arguments,console,false,ct);
            }
            var detail=combined.Trim(); if(detail.Length>1200) detail=detail[^1200..];
            return new(false,p.ExitCode,$"SteamCMD exited with code {p.ExitCode}. {detail}");
        }
        catch(Exception ex) { console?.Report($"[ERROR] {ex.Message}"); return new(false,null,$"Could not execute SteamCMD: {ex.Message}"); }
    }

    public async Task<string> InstallAsync(string installDirectory, IProgress<double>? progress=null, IProgress<string>? status=null, IProgress<string>? console=null, CancellationToken ct=default)
    {
        if(string.IsNullOrWhiteSpace(installDirectory)) throw new ArgumentException("Select an installation directory.");
        installDirectory=Path.GetFullPath(installDirectory); Directory.CreateDirectory(installDirectory);
        var url=OperatingSystem.IsWindows()?WindowsDownloadUrl:OperatingSystem.IsLinux()?LinuxDownloadUrl:throw new PlatformNotSupportedException();
        var archive=Path.Combine(Path.GetTempPath(),OperatingSystem.IsWindows()?"jaasm-steamcmd.zip":"jaasm-steamcmd.tar.gz");
        console?.Report($"[DOWNLOAD] {url}"); console?.Report($"[TARGET] {installDirectory}");
        if(File.Exists(archive)) File.Delete(archive);
        using var response=await _http.GetAsync(url,HttpCompletionOption.ResponseHeadersRead,ct); response.EnsureSuccessStatusCode();
        var length=response.Content.Headers.ContentLength; long total=0;
        await using(var input=await response.Content.ReadAsStreamAsync(ct))
        await using(var output=File.Create(archive))
        {
            var buffer=new byte[81920]; int read;
            while((read=await input.ReadAsync(buffer,ct))>0) { await output.WriteAsync(buffer.AsMemory(0,read),ct); total+=read; if(length is>0) progress?.Report((double)total/length.Value); }
        }
        console?.Report($"[DOWNLOAD COMPLETE] {total:N0} bytes");
        if(total<=0 || (length is>0 && total!=length.Value)) throw new IOException("SteamCMD download is incomplete.");
        status?.Report($"Extracting SteamCMD to {installDirectory}...");
        if(OperatingSystem.IsWindows()) ZipFile.ExtractToDirectory(archive,installDirectory,true);
        else
        {
            await using var compressed=File.OpenRead(archive); await using var gzip=new GZipStream(compressed,CompressionMode.Decompress); TarFile.ExtractToDirectory(gzip,installDirectory,true);
            var script=Path.Combine(installDirectory,ExecutableName); if(File.Exists(script)) File.SetUnixFileMode(script,UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute|UnixFileMode.GroupRead|UnixFileMode.GroupExecute|UnixFileMode.OtherRead|UnixFileMode.OtherExecute);
        }
        var exe=Path.Combine(installDirectory,ExecutableName); if(!File.Exists(exe)) throw new FileNotFoundException($"{ExecutableName} missing after extraction.");
        console?.Report($"[EXTRACTED] {exe}"); status?.Report("Running SteamCMD first-launch validation...");
        var validation=await ValidateDetailedAsync(exe,console,ct); if(!validation.Success) throw new InvalidOperationException(validation.Message);
        File.Delete(archive); status?.Report("SteamCMD installation and validation passed."); console?.Report("[READY] SteamCMD validated.");
        return exe;
    }
}
