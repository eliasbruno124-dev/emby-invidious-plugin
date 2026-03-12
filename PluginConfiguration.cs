using Emby.Web.GenericEdit;
using System.ComponentModel;

namespace Emby.InvidiousPlugin
{
    public class PluginConfiguration : EditableOptionsBase
    {
        public override string EditorTitle => "Invidious Settings";

        [DisplayName("My Invidious Instance URL")]
        [Description("Enter the URL of your self-hosted instance (e.g. http://localhost:3000)")]
        public string InvidiousUrl { get; set; } = "http://localhost:3000";

        [DisplayName("Use yt-dlp Proxy")]
        [Description("Use the local yt-dlp/ffmpeg proxy for playback")]
        public bool UseYtProxy { get; set; } = true;

        [DisplayName("yt-dlp Proxy URL")]
        [Description("Example: http://localhost:8080")]
        public string YtProxyUrl { get; set; } = "http://localhost:8080";

        [DisplayName("Max Videos for Channels/Playlists")]
        public int MaxChannelVideos { get; set; } = 60;

        [DisplayName("Max Videos for Search")]
        public int MaxSearchVideos { get; set; } = 150;

        [DisplayName("My YouTube Content (Comma-separated)")]
        [Description("Channels: @Handle | Playlists: PL... | Searches: regular words")]
        public string SavedItems { get; set; } = "@GitHub, PL0lo9MOBetEFcp4SCWinBdpml9B2U25-f, Minecraft Trailer";
    }
}