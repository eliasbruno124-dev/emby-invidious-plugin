using Emby.Web.GenericEdit;
using System.ComponentModel;

namespace Emby.InvidiousPlugin
{
    public class PluginConfiguration : EditableOptionsBase
    {
        public override string EditorTitle => "Invidious Plugin Settings";

        [DisplayName("My Invidious Instance URL")]
        [Description("Enter the URL of your self-hosted Invidious instance (e.g. http://localhost:3000). For Basic Auth use: https://User:Password@invidious.example.com")]
        public string InvidiousUrl { get; set; } = "";

        [DisplayName("My YouTube Content")]
        [Description("Separate entries with a comma: @Handle for channels, PLxxx for playlists, or plain text for search queries. Example: @GitHub, PL0lo9MOBetEFcp4SCWinBdpml9B2U25-f, Linux Tutorials")]
        public string SavedItems { get; set; } = "";

        [DisplayName("Max Videos (Channels & Playlists)")]
        [Description("Maximum number of videos to load per channel or playlist (1-150).")]
        public int MaxChannelVideos { get; set; } = 50;

        [DisplayName("Max Videos (Search)")]
        [Description("Maximum number of videos to load per search query (1-150).")]
        public int MaxSearchVideos { get; set; } = 50;
    }
}
