using System.Diagnostics;

namespace JAASM.App.Services;

public sealed record AsaProcessState(bool Running, int? ProcessId, TimeSpan? Uptime, DateTimeOffset? StartedAt, string Message);

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
            return new(false, null, null, null, "Stopped");

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
            return new(false, null, null, null, $"ASA executable not found: {executablePath}");

        var launcher = executablePath;
        var psi = new ProcessStartInfo
        {
            WorkingDirectory = Path.GetDirectoryName(executablePath)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        if (OperatingSystem.IsLinux())
        {
            var wine = ResolveWineExecutable();
            if (wine is null)
                return new(false, null, null, null,
                    "Wine was not found. Install Wine and ensure 'wine' or 'wine64' is available in PATH.");

            launcher = wine;
            psi.FileName = wine;
            // ArgumentList preserves paths containing spaces without shell-style escaping.
            psi.ArgumentList.Add(executablePath);
            foreach (var argument in SplitLaunchArguments(launchArguments))
                psi.ArgumentList.Add(argument);

            console?.Report($"[{profileId}] [WINE] Runtime: {wine}");
            console?.Report($"[{profileId}] [ASA START] {wine} \"{executablePath}\" {launchArguments}");
        }
        else
        {
            psi.FileName = executablePath;
            psi.Arguments = launchArguments;
            console?.Report($"[{profileId}] [ASA START] {Path.GetFileName(executablePath)} {launchArguments}");
        }

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };

        try
        {
            if (!process.Start())
                return new(false, null, null, null, "Operating system could not start the ASA server process.");

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
            return new(false, null, null, null, ex.Message);
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

    private static IEnumerable<string> SplitLaunchArguments(string commandLine)
    {
        var current = new System.Text.StringBuilder();
        var quoted = false;

        foreach (var ch in commandLine)
        {
            if (ch == '"')
            {
                quoted = !quoted;
                continue;
            }

            if (char.IsWhiteSpace(ch) && !quoted)
            {
                if (current.Length > 0)
                {
                    yield return current.ToString();
                    current.Clear();
                }
                continue;
            }

            current.Append(ch);
        }

        if (current.Length > 0)
            yield return current.ToString();
    }

    private static string? ResolveWineExecutable()
    {
        if (!OperatingSystem.IsLinux())
            return null;

        foreach (var candidate in new[] { "wine64", "wine" })
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = candidate,
                    Arguments = "--version",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using var process = Process.Start(psi);
                if (process is null)
                    continue;

                process.WaitForExit(3000);
                if (process.HasExited && process.ExitCode == 0)
                    return candidate;
            }
            catch
            {
                // Try the next Wine executable.
            }
        }

        return null;
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
