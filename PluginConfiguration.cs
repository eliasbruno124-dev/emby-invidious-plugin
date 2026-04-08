using Emby.Web.GenericEdit;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

namespace Emby.InvidiousPlugin
{
    public class PluginConfiguration : EditableOptionsBase
    {
        public override string EditorTitle => "Invidious Plugin Settings";

        // ─────────────────────────────────────────────────────────────────────
        // CONNECTION
        // ─────────────────────────────────────────────────────────────────────

        [DisplayName("Invidious Instance URL")]
        [Description(
            "The URL of your self-hosted Invidious instance.\n" +
            "Examples:\n" +
            "  • http://localhost:3000\n" +
            "  • https://invidious.example.com\n" +
            "  • https://User:Password@invidious.example.com  (Basic Auth)")]
        public string InvidiousUrl { get; set; } = "";

        // ─────────────────────────────────────────────────────────────────────
        // CONTENT SOURCES
        // ─────────────────────────────────────────────────────────────────────

        [DisplayName("My YouTube Content")]
        [Description(
            "Comma-separated list of content sources. Three types are supported:\n" +
            "  • @Handle  — a YouTube channel handle (e.g. @GitHub)\n" +
            "  • UCxxxxxx — a YouTube channel ID (e.g. UCVHFbw7woebKtYXvKgG-Z6Q)\n" +
            "  • PLxxxxxx — a playlist ID (e.g. PL0lo9MOBetEFcp4SCWinBdpml9B2U25-f)\n" +
            "  • any text — a search query (e.g. Linux Tutorials)\n\n" +
            "Example: @GitHub, PLxxxxxx, Linux Tutorials")]
        public string SavedItems { get; set; } = "";

        // ─────────────────────────────────────────────────────────────────────
        // DISCOVER
        // ─────────────────────────────────────────────────────────────────────

        [DisplayName("Show Trending")]
        [Description("Show a 'Trending' folder with trending and popular videos from YouTube.")]
        public bool ShowTrending { get; set; } = true;

        [DisplayName("Trending Region")]
        [Description(
            "ISO 3166-1 country code for YouTube trending results.\n" +
            "Examples: DE (Germany), US (USA), AT (Austria), CH (Switzerland), GB (UK), FR (France).\n" +
            "Leave empty for Invidious default.")]
        public string TrendingRegion { get; set; } = "DE";

        // ─────────────────────────────────────────────────────────────────────
        // LIMITS
        // ─────────────────────────────────────────────────────────────────────

        [DisplayName("Max Videos per Channel or Playlist")]
        [Description("Maximum number of videos loaded per channel or playlist. Range: 1–150.")]
        [Range(1, 150)]
        public int MaxChannelVideos { get; set; } = 50;

        [DisplayName("Max Videos per Search Query")]
        [Description("Maximum number of videos loaded per search query. Range: 1–150.")]
        [Range(1, 150)]
        public int MaxSearchVideos { get; set; } = 50;

        // ─────────────────────────────────────────────────────────────────────
        // CACHING
        // ─────────────────────────────────────────────────────────────────────

        [DisplayName("HLS Cache Duration (Days)")]
        [Description(
            "How many days to keep muxed HLS segments on disk (used for 1080p and 4K streams).\n" +
            "Set to 0 to disable caching — each playback will re-mux from scratch.\n" +
            "Recommended: 3 days. Higher values save bandwidth but use more disk space.")]
        [Range(0, 30)]
        public int CacheDays { get; set; } = 3;
    }
}
