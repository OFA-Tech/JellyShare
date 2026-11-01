namespace JellyShare.Domain.Exceptions;

/// <summary>The peer state file exists but cannot be read.</summary>
public sealed class PeerStateUnreadableException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="PeerStateUnreadableException"/> class.</summary>
    /// <param name="message">The message.</param>
    /// <param name="inner">The cause.</param>
    public PeerStateUnreadableException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}
