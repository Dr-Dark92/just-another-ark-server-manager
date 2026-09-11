using System.Diagnostics;

namespace JAASM.App.Services;

public sealed record AsaProcessState(bool Running, int? ProcessId, TimeSpan? Uptime, string Message);

public sealed class AsaProcessService : IDisposable
{
    private sealed class ManagedProcess
    {
        public required Process Process { get; init; }
        public required DateTimeOffset StartedAt { get; init; }
    }

    private readonly Dictionary<string, ManagedProcess> _processes =
        new(StringComparer.OrdinalIgnoreCase);

    public AsaProcessState GetState(string profileId)
    {
        if (!_processes.TryGetValue(profileId, out var managed) || managed.Process.HasExited)
            return new(false, null, null, "Stopped");

        return new(true, managed.Process.Id,
            DateTimeOffset.Now - managed.StartedAt, "Running");
    }

    public async Task<AsaProcessState> StartAsync(
        string profileId, string executablePath, string launchArguments,
        IProgress<string>? console = null, CancellationToken ct = default)
    {
        var current = GetState(profileId);
        if (current.Running)
            return current;

        if (!File.Exists(executablePath))
            return new(false, null, null, $"ASA executable not found: {executablePath}");

        var psi = new ProcessStartInfo
        {
            FileName = executablePath,
            Arguments = launchArguments,
            WorkingDirectory = Path.GetDirectoryName(executablePath)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        console?.Report($"[{profileId}] [ASA START] {Path.GetFileName(executablePath)} {launchArguments}");

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };

        try
        {
            if (!process.Start())
                return new(false, null, null, "Operating system could not start the ASA server process.");

            var managed = new ManagedProcess { Process = process, StartedAt = DateTimeOffset.Now };
            _processes[profileId] = managed;

            process.Exited += (_, _) =>
            {
                console?.Report($"[{profileId}] [ASA EXITED] Exit code: {process.ExitCode}");
            };

            _ = PumpAsync(process.StandardOutput, $"[{profileId}] [ASA]", console, ct);
            _ = PumpAsync(process.StandardError, $"[{profileId}] [ASA STDERR]", console, ct);

            console?.Report($"[{profileId}] [ASA RUNNING] PID {process.Id}");
            return GetState(profileId);
        }
        catch (Exception ex)
        {
            console?.Report($"[{profileId}] [ASA START ERROR] {ex.Message}");
            process.Dispose();
            _processes.Remove(profileId);
            return new(false, null, null, ex.Message);
        }
    }

    public async Task<AsaProcessState> StopAsync(
        string profileId, IProgress<string>? console = null, CancellationToken ct = default)
    {
        if (!_processes.TryGetValue(profileId, out var managed) || managed.Process.HasExited)
            return GetState(profileId);

        var process = managed.Process;
        console?.Report($"[{profileId}] [ASA STOP] Requesting shutdown for PID {process.Id}...");

        try
        {
            if (process.CloseMainWindow())
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(20));
                try { await process.WaitForExitAsync(timeout.Token); } catch (OperationCanceledException) { }
            }

            if (!process.HasExited)
            {
                console?.Report($"[{profileId}] [ASA STOP] Graceful close unavailable; terminating process.");
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(ct);
            }

            return GetState(profileId);
        }
        catch (Exception ex)
        {
            console?.Report($"[{profileId}] [ASA STOP ERROR] {ex.Message}");
            return GetState(profileId) with { Message = ex.Message };
        }
    }

    public async Task<AsaProcessState> ForceStopAsync(
        string profileId, IProgress<string>? console = null, CancellationToken ct = default)
    {
        if (!_processes.TryGetValue(profileId, out var managed) || managed.Process.HasExited)
            return GetState(profileId);

        console?.Report($"[{profileId}] [ASA FORCE STOP] Killing PID {managed.Process.Id} and child processes.");
        managed.Process.Kill(entireProcessTree: true);
        await managed.Process.WaitForExitAsync(ct);
        return GetState(profileId);
    }

    public async Task<AsaProcessState> RestartAsync(
        string profileId, string executablePath, string launchArguments,
        IProgress<string>? console = null, CancellationToken ct = default)
    {
        await StopAsync(profileId, console, ct);
        return await StartAsync(profileId, executablePath, launchArguments, console, ct);
    }

    private static async Task PumpAsync(StreamReader reader, string prefix,
        IProgress<string>? console, CancellationToken ct)
    {
        try
        {
            while (await reader.ReadLineAsync(ct) is { } line)
                console?.Report($"{prefix} {line}");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { console?.Report($"{prefix} [STREAM ERROR] {ex.Message}"); }
    }

    public void Dispose()
    {
        foreach (var managed in _processes.Values)
            managed.Process.Dispose();
        _processes.Clear();
        GC.SuppressFinalize(this);
    }
}
