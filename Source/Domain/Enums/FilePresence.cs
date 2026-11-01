namespace JellyShare.Domain.Enums;

/// <summary>Result of a file existence probe.</summary>
public enum FilePresence
{
    /// <summary>The file exists.</summary>
    Present,

    /// <summary>The file or its folder does not exist.</summary>
    Missing,

    /// <summary>The probe failed (permissions, I/O error); treated as present.</summary>
    Unknown,
}
