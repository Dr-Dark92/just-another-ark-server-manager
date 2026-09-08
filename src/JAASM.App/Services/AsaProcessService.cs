using System.Diagnostics;

namespace JAASM.App.Services;

public sealed record AsaProcessState(bool Running, int? ProcessId, TimeSpan? Uptime, string Message);

public sealed class AsaProcessService : IDisposable
{
    private Process? _process;
    private DateTimeOffset? _startedAt;

    public AsaProcessState GetState()
    {
        if (_process is null || _process.HasExited)
            return new(false, null, null, "Stopped");

        return new(true, _process.Id,
            _startedAt is null ? null : DateTimeOffset.Now - _startedAt.Value,
            "Running");
    }

    public async Task<AsaProcessState> StartAsync(
        string executablePath,
        string launchArguments,
        IProgress<string>? console = null,
        CancellationToken ct = default)
    {
        if (_process is { HasExited: false })
            return GetState();

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

        console?.Report($"[ASA START] {Path.GetFileName(executablePath)} {launchArguments}");

        _process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        _process.Exited += (_, _) =>
        {
            console?.Report($"[ASA EXITED] Exit code: {_process?.ExitCode}");
            _startedAt = null;
        };

        try
        {
            if (!_process.Start())
                return new(false, null, null, "Windows could not start the ASA server process.");

            _startedAt = DateTimeOffset.Now;
            _ = PumpAsync(_process.StandardOutput, "[ASA]", console, ct);
            _ = PumpAsync(_process.StandardError, "[ASA STDERR]", console, ct);

            console?.Report($"[ASA RUNNING] PID {_process.Id}");
            return GetState();
        }
        catch (Exception ex)
        {
            console?.Report($"[ASA START ERROR] {ex.Message}");
            _process.Dispose();
            _process = null;
            _startedAt = null;
            return new(false, null, null, ex.Message);
        }
    }

    public async Task<AsaProcessState> StopAsync(
        IProgress<string>? console = null,
        CancellationToken ct = default)
    {
        if (_process is null || _process.HasExited)
            return GetState();

        console?.Report($"[ASA STOP] Requesting shutdown for PID {_process.Id}...");

        try
        {
            // Until RCON graceful shutdown is added, first request a normal window/process close.
            if (_process.CloseMainWindow())
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(20));
                try { await _process.WaitForExitAsync(timeout.Token); } catch (OperationCanceledException) { }
            }

            if (!_process.HasExited)
            {
                console?.Report("[ASA STOP] Graceful close unavailable; terminating process.");
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync(ct);
            }

            return GetState();
        }
        catch (Exception ex)
        {
            console?.Report($"[ASA STOP ERROR] {ex.Message}");
            return GetState() with { Message = ex.Message };
        }
    }

    public async Task<AsaProcessState> ForceStopAsync(
        IProgress<string>? console = null,
        CancellationToken ct = default)
    {
        if (_process is null || _process.HasExited)
            return GetState();

        console?.Report($"[ASA FORCE STOP] Killing PID {_process.Id} and child processes.");
        _process.Kill(entireProcessTree: true);
        await _process.WaitForExitAsync(ct);
        return GetState();
    }

    public async Task<AsaProcessState> RestartAsync(
        string executablePath,
        string launchArguments,
        IProgress<string>? console = null,
        CancellationToken ct = default)
    {
        await StopAsync(console, ct);
        return await StartAsync(executablePath, launchArguments, console, ct);
    }

    private static async Task PumpAsync(
        StreamReader reader,
        string prefix,
        IProgress<string>? console,
        CancellationToken ct)
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
        _process?.Dispose();
        GC.SuppressFinalize(this);
    }
}
