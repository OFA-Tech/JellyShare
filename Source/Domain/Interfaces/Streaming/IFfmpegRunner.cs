using JellyShare.Domain.Models.Streaming;

namespace JellyShare.Domain.Interfaces.Streaming;

/// <summary>Runs ffmpeg and copies its stdout to a stream.</summary>
public interface IFfmpegRunner
{
    /// <summary>Runs the executable until it exits or <paramref name="cancellationToken"/> fires (then it is killed).</summary>
    /// <param name="executable">The executable.</param>
    /// <param name="arguments">The arguments, passed without a shell.</param>
    /// <param name="output">Where stdout is copied.</param>
    /// <param name="cancellationToken">Cancelled when the client disconnects.</param>
    /// <returns>The run result.</returns>
    Task<FfmpegRunResult> RunAsync(string executable, IReadOnlyList<string> arguments, Stream output, CancellationToken cancellationToken);
}
