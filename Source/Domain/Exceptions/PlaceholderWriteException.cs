using JellyShare.Domain.Models.Placeholders;

namespace JellyShare.Domain.Exceptions;

/// <summary>Thrown when applying a diff fails part-way; <see cref="Partial"/> holds what was already changed.</summary>
public sealed class PlaceholderWriteException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="PlaceholderWriteException"/> class.</summary>
    /// <param name="partial">What was done before the failure.</param>
    /// <param name="inner">The cause.</param>
    public PlaceholderWriteException(PlaceholderWriteResult partial, Exception inner)
        : base("Applying placeholder changes failed part-way", inner)
    {
        Partial = partial;
    }

    /// <summary>Gets what was done before the failure.</summary>
    public PlaceholderWriteResult Partial { get; }
}
