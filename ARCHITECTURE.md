# Architecture — Emby Invidious Plugin

## Overview

A privacy-friendly Emby plugin that integrates YouTube content via a self-hosted [Invidious](https://github.com/iv-org/invidious/) instance. No Google API keys required. Supports channels, playlists, search, trending, Shorts, and livestreams with full HLS muxing.

## Code Structure

```
Emby-Invidious-Plugin/
├── Plugin.cs                 # Plugin entry point, singleton instance, version display
├── PluginConfiguration.cs    # All user-facing settings (EditableOptionsBase)
├── InvidiousChannel.cs       # Channel provider — folder structure, video listing,
│                              #   media info, Shorts/Live detection, thumbnails
├── InvidiousApi.cs            # Static HTTP client, retry logic, all Invidious API
│                              #   calls, thumbnail URL rewriting
├── MuxHelper.cs               # FFmpeg HLS muxing, process lifecycle, segment caching,
│                              #   watchdog, session detection, playback.m3u8 management
├── thumb.png                  # Plugin logo shown in Emby dashboard
└── Emby.InvidiousPlugin.csproj
```

## Key Components

### Plugin.cs

Plugin entry point implementing `BasePluginSimpleUI<PluginConfiguration>`. Holds the singleton `Instance`, resolves `ISessionManager` for playback session tracking, and exposes the plugin version from the assembly.

### InvidiousChannel.cs

Implements `IChannel` to provide the folder/video structure Emby displays. Responsibilities:

- Resolves `@Handle`, `UC...` channel IDs, `PL...` playlists, and search queries
- Fetches channel avatars and playlist thumbnails for the main menu
- Detects Shorts (duration, aspect ratio, API flags) and livestreams (`liveNow`)
- Builds `ChannelMediaInfo` entries with direct MP4 (480p/720p) and HLS (1080p/4K) sources
- Trending folder: parallel fetch of Popular, Trending, Music, Gaming, Movies — deduplicated

### InvidiousApi.cs

Static HTTP client with automatic retry and error handling for all Invidious REST endpoints (`/api/v1/videos/`, `/channels/`, `/search`, `/trending`). Rewrites thumbnail URLs to proxy through the configured instance.

### MuxHelper.cs

Manages FFmpeg processes that mux adaptive video + audio into HLS segments. Key features:

- **Watchdog loop**: Monitors FFmpeg health, segment progress, and viewer sessions
- **Session detection**: Checks `ISessionManager` for active playback (`IsVideoBeingPlayed`) and HTTP-access heartbeat (`TouchAccess`/`HasRecentAccess`)
- **Pre-buffer**: Muxes a configurable number of segments ahead, then pauses if no viewer is detected
- **Cache management**: Segments persist on disk for configurable days; stale caches are cleaned on startup
- **Error recovery**: Handles FFmpeg crashes, 503 errors from Invidious, and implements resume cooldowns
- **Discontinuity stripping**: Removes `#EXT-X-DISCONTINUITY` tags from playlists after FFmpeg reconnects

### PluginConfiguration.cs

All settings exposed via Emby's plugin configuration UI: instance URL, content sources, trending region, sort order, quality (4K toggle), cache duration, FFmpeg path, and pre-buffer size.

## Design Patterns

- **Singleton**: `Plugin.Instance` provides global access to configuration and session manager
- **Static helpers**: `InvidiousApi` and `MuxHelper` use static methods/dictionaries for process-wide state (active mux jobs, caches, cooldowns)
- **Concurrent collections**: `ConcurrentDictionary` for mux job tracking, access timestamps, and resume cooldowns
