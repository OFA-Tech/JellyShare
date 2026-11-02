namespace JellyShare.Domain.Enums;

/// <summary>Video encoder used for capped streams.</summary>
public enum HardwareEncoder
{
    /// <summary>Software libx264.</summary>
    None,

    /// <summary>NVIDIA NVENC.</summary>
    Nvenc,

    /// <summary>Intel Quick Sync.</summary>
    Qsv,

    /// <summary>VA-API.</summary>
    Vaapi,

    /// <summary>Apple VideoToolbox.</summary>
    VideoToolbox,

    /// <summary>AMD AMF.</summary>
    Amf,
}
