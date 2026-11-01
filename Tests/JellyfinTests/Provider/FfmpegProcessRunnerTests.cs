using System.Diagnostics;
using JellyShare.Jellyfin.Provider;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace JellyShare.JellyfinTests.Provider;

/// <summary>Runs real child processes to prove output copying and that cancellation kills the process.</summary>
public sealed class FfmpegProcessRunnerTests
{
    private static (string Exe, string[] Args) Endless()
        => OperatingSystem.IsWindows()
            ? ("ping.exe", new[] { "-n", "60", "127.0.0.1" })
            : ("/bin/sh", new[] { "-c", "while :; do echo x; sleep 0.1; done" });

    private static (string Exe, string[] Args) Short()
        => OperatingSystem.IsWindows()
            ? ("cmd.exe", new[] { "/c", "echo hello" })
            : ("/bin/sh", new[] { "-c", "echo hello" });

    [Fact]
    public async Task Run_CopiesStdout_AndReturnsExitCode()
    {
        var (exe, args) = Short();
        using var output = new MemoryStream();

        var result = await new FfmpegProcessRunner(NullLogger<FfmpegProcessRunner>.Instance).RunAsync(exe, args, output, CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(output.Length, result.BytesWritten);
        Assert.StartsWith("hello", System.Text.Encoding.ASCII.GetString(output.ToArray()));
    }

    [Fact]
    public async Task Run_Cancelled_KillsProcessPromptly()
    {
        var (exe, args) = Endless();
        var output = new FirstWriteStream();
        using var cts = new CancellationTokenSource();
        var runner = new FfmpegProcessRunner(NullLogger<FfmpegProcessRunner>.Instance);

        var run = runner.RunAsync(exe, args, output, cts.Token);
        await output.FirstWrite.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var pid = runner.LastProcessId;
        var clock = Stopwatch.StartNew();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(TimeSpan.FromSeconds(10)));

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5));
        Assert.NotNull(pid);
        Assert.Throws<ArgumentException>(() => Process.GetProcessById(pid!.Value));
    }

    [Fact]
    public async Task Run_MissingExecutable_Throws()
    {
        await Assert.ThrowsAnyAsync<Exception>(() => new FfmpegProcessRunner(NullLogger<FfmpegProcessRunner>.Instance)
            .RunAsync(Path.Combine(Path.GetTempPath(), "no-such-ffmpeg-" + Guid.NewGuid().ToString("N")), Array.Empty<string>(), Stream.Null, CancellationToken.None));
    }

    private sealed class FirstWriteStream : MemoryStream
    {
        public TaskCompletionSource FirstWrite { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            FirstWrite.TrySetResult();
            return base.WriteAsync(buffer, cancellationToken);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            FirstWrite.TrySetResult();
            return base.WriteAsync(buffer, offset, count, cancellationToken);
        }
    }
}
