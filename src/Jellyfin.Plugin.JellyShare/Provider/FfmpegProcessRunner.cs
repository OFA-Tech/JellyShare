using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyShare.Provider;

/// <summary>Runs ffmpeg as a child process without a shell; the process tree is killed when the run is cancelled.</summary>
public sealed class FfmpegProcessRunner : IFfmpegRunner
{
    private const int ErrorTailLength = 2048;
    private static readonly TimeSpan ExitWait = TimeSpan.FromSeconds(5);

    private readonly ILogger<FfmpegProcessRunner> _logger;

    /// <summary>Initializes a new instance of the <see cref="FfmpegProcessRunner"/> class.</summary>
    /// <param name="logger">The logger.</param>
    public FfmpegProcessRunner(ILogger<FfmpegProcessRunner> logger)
    {
        _logger = logger;
    }

    /// <summary>Gets the id of the most recently started process (for tests).</summary>
    internal int? LastProcessId { get; private set; }

    /// <inheritdoc />
    public async Task<FfmpegRunResult> RunAsync(string executable, IReadOnlyList<string> arguments, Stream output, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            RedirectStandardInput = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        process.Start();
        LastProcessId = process.Id;
        var errors = DrainAsync(process.StandardError);
        long written = 0;

        // Pipe reads do not always observe the token, so cancellation kills the process, which ends the read.
        using var killOnCancel = cancellationToken.Register(() => KillTree(process));
        try
        {
            var buffer = new byte[81920];
            var stdout = process.StandardOutput.BaseStream;
            int read;
            while ((read = await stdout.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                written += read;
            }

            cancellationToken.ThrowIfCancellationRequested();
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            var tail = await errors.ConfigureAwait(false);
            return new FfmpegRunResult(process.ExitCode, written, tail);
        }
        finally
        {
            Kill(process);
        }
    }

    private static void KillTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
        }
    }

    private void Kill(Process process)
    {
        try
        {
            KillTree(process);
            if (!process.WaitForExit(ExitWait))
            {
                _logger.LogWarning("ffmpeg process {Pid} did not exit after kill", process.Id);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            _logger.LogDebug(ex, "Could not kill ffmpeg process");
        }
    }

    private static async Task<string> DrainAsync(StreamReader reader)
    {
        var tail = new System.Text.StringBuilder();
        var buffer = new char[4096];
        try
        {
            int read;
            while ((read = await reader.ReadAsync(buffer).ConfigureAwait(false)) > 0)
            {
                tail.Append(buffer, 0, read);
                if (tail.Length > ErrorTailLength * 2)
                {
                    tail.Remove(0, tail.Length - ErrorTailLength);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
        }

        return tail.Length > ErrorTailLength ? tail.ToString(tail.Length - ErrorTailLength, ErrorTailLength) : tail.ToString();
    }
}
