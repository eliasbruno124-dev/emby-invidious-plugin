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

        [DisplayName("Watch Later Playlist")]
        [Description(
            "Playlist ID for a '⭐ Watch Later' folder with live refresh (~10 seconds).\n" +
            "Add videos to this playlist on Invidious and they appear in Emby within ~15 seconds.\n" +
            "Only the Invidious channel is refreshed — other channels stay untouched.\n" +
            "Example: PLxxxxxx")]
        public string WatchLaterPlaylist { get; set; } = "";

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
        public string TrendingRegion { get; set; } = "";

        // ─────────────────────────────────────────────────────────────────────
        // SORTING
        // ─────────────────────────────────────────────────────────────────────

        [DisplayName("Sort Channel Videos By")]
        [Description(
            "How to sort videos when browsing a channel.\n" +
            "Options: newest, oldest, popular")]
        public string ChannelSortBy { get; set; } = "newest";

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
        // QUALITY
        // ─────────────────────────────────────────────────────────────────────

        [DisplayName("Enable 4K (2160p)")]
        [Description(
            "Enable 4K video quality. When disabled, the maximum HLS quality is 1080p.\n" +
            "Disable this on hardware-weak systems to save CPU and bandwidth,\n" +
            "as 4K VP9 muxing is significantly more demanding.")]
        public bool Enable4K { get; set; } = false;

        // ─────────────────────────────────────────────────────────────────────
        // CACHING
        // ─────────────────────────────────────────────────────────────────────

        [DisplayName("HLS Cache Duration (Days)")]
        [Description(
            "How many days to keep muxed HLS segments on disk (used for 1080p and 4K streams).\n" +
            "Set to 0 to delete all caches and always re-mux from scratch.\n" +
            "Recommended: 3 days. Higher values save bandwidth but use more disk space.")]
        [Range(0, 30)]
        public int CacheDays { get; set; } = 3;

        // ─────────────────────────────────────────────────────────────────────
        // ADVANCED / PERFORMANCE
        // ─────────────────────────────────────────────────────────────────────

        [DisplayName("FFmpeg Path")]
        [Description(
            "Custom path to the FFmpeg executable.\n" +
            "Leave empty to auto-detect (searches PATH and common install locations).\n" +
            "Examples:\n" +
            "  • C:\\ffmpeg\\bin\\ffmpeg.exe\n" +
            "  • /usr/bin/ffmpeg")]
        public string FfmpegPath { get; set; } = "";

        [DisplayName("Pre-Buffer Segments")]
        [Description(
            "Max HLS segments to pre-buffer when no one is actively watching.\n" +
            "Each segment ≈ 4 seconds. Default: 90 (~6 min buffer).\n" +
            "Lower = less disk/bandwidth, higher = less re-buffering.\n" +
            "Range: 10–300.")]
        [Range(10, 300)]
        public int PreBufferSegments { get; set; } = 90;

        [DisplayName("Session Grace Period (Seconds)")]
        [Description(
            "Seconds to keep muxing after the last viewer stops watching.\n" +
            "Prevents unnecessary stop+resume cycles during brief pauses.\n" +
            "Default: 15. Range: 5–120.")]
        [Range(5, 120)]
        public int SessionGraceSeconds { get; set; } = 15;

        [DisplayName("Idle Timeout (Seconds)")]
        [Description(
            "Stop FFmpeg if no new segment is produced within this time (stall detection).\n" +
            "Default: 30. Range: 15–300.")]
        [Range(15, 300)]
        public int IdleTimeoutSeconds { get; set; } = 30;
    }
}
