using Emby.Web.GenericEdit;
using System.ComponentModel;

namespace Emby.InvidiousPlugin
{
    public class PluginConfiguration : EditableOptionsBase
    {
        public override string EditorTitle => "Invidious Settings";

        [DisplayName("Invidious Instance URL")]
        public string InvidiousUrl { get; set; } = "https://yewtu.be";

        [DisplayName("My Search Terms (Comma-separated)")]
        [Description("e.g., Minecraft, News, Trailer")]
        public string SavedSearches { get; set; } = "Minecraft, Trailer";

        [DisplayName("My Channels (Channel IDs, Comma-separated)")]
        [Description("IMPORTANT: Use the Channel ID (usually starts with UC...). e.g., UCBcRF18a7Qf58cCRy5xgHKQ")]
        public string SavedChannels { get; set; } = "";

        [DisplayName("My Playlists (Playlist IDs, Comma-separated)")]
        [Description("IMPORTANT: Use the Playlist ID (usually starts with PL...). e.g., PLBCF2DAC6FFB574DE")]
        public string SavedPlaylists { get; set; } = "";
    }
}