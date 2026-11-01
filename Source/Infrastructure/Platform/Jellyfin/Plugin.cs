using JellyShare.Jellyfin.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace JellyShare.Jellyfin;

/// <summary>
/// The JellyShare plugin entry point.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>The plugin GUID. Must match <c>build.yaml</c>.</summary>
    public static readonly Guid PluginId = new("8a55d113-5072-47cf-9827-f3c49f1c3971");

    /// <summary>The plugin display name.</summary>
    public const string PluginName = "JellyShare";

    /// <summary>Initializes a new instance of the <see cref="Plugin"/> class.</summary>
    /// <param name="applicationPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="xmlSerializer">Instance of the <see cref="IXmlSerializer"/> interface.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
        ApplyPlaceholderDefaults();
        ConfigurationChanged += (_, _) => ApplyPlaceholderDefaults();
    }

    /// <summary>Gets the current plugin instance.</summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public override string Name => PluginName;

    /// <inheritdoc />
    public override Guid Id => PluginId;

    private void ApplyPlaceholderDefaults()
        => PluginConfiguration.ResolvePlaceholderRoots(Configuration, DataFolderPath);

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        var ns = GetType().Namespace;
        return
        [
            new PluginPageInfo
            {
                Name = PluginName,
                DisplayName = PluginName,
                EmbeddedResourcePath = ns + ".Configuration.configPage.html",
                EnableInMainMenu = true,
                MenuSection = "server",
                MenuIcon = "share",
            },
            new PluginPageInfo
            {
                Name = ScriptPageName,
                EmbeddedResourcePath = ns + ".Configuration.configPage.js",
            },
        ];
    }

    /// <summary>The name under which the admin page script is served (<c>configurationpage?name=…</c>).</summary>
    public const string ScriptPageName = "JellyShareScript";
}
