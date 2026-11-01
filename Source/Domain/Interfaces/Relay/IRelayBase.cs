namespace JellyShare.Domain.Interfaces.Relay;

/// <summary>Provides the loopback relay base URL written into placeholder <c>.strm</c> files.</summary>
public interface IRelayBase
{
    /// <summary>Gets the relay base URL, without a trailing slash.</summary>
    string Current { get; }
}
