# 📺 Emby Invidious Plugin

A powerful, privacy-friendly [Invidious](https://github.com/iv-org/invidious/) plugin for [Emby](https://emby.media/) that operates entirely without Google API keys. It uses an Invidious instance (ideally self-hosted) to seamlessly integrate channels, playlists, search queries, and trending content directly into your Emby dashboard — with full 4K/1080p HLS muxing, Shorts support, and livestream playback.

Plugin Screenshot

<img width="1919" height="533" alt="Screenshot 2026-04-09 140615" src="https://github.com/user-attachments/assets/b4a0082e-61f2-4abd-8598-856d32774a8a" />

<img width="1919" height="306" alt="overview" src="https://github.com/user-attachments/assets/47a8fb20-2496-45f9-845c-cd534883360b" />

<img width="1891" height="716" alt="Trending" src="https://github.com/user-attachments/assets/5d6af148-c007-4faa-9672-b1ab2720a3dd" />

<img width="1532" height="353" alt="4k" src="https://github.com/user-attachments/assets/b8f1e9e5-0084-4031-8103-6eaf99adf926" />

## ✨ Features

### Core

* **🛡️ 100% Privacy:** No Google trackers, no API keys required. Everything runs locally through your Invidious instance.
* **🔄 Seamless Playlist Syncing:** Log into your Invidious account and create a **public playlist**. Add its `PL...` ID to this plugin. Any video you add to that playlist via Invidious will automatically sync to your Emby dashboard!
* **⭐ Watch Later with Live Refresh:** Configure a dedicated Watch Later playlist — the plugin polls it every **10 seconds** and refreshes only the Invidious channel when changes are detected. Add a video on Invidious, see it in Emby within ~15 seconds. No other channels are affected.
* **🎯 Smart "@" Input:** A single, clean text field for all your content! Type `@GitHub` for channels, `PL123...` for playlists, or regular words for search queries.
* **🖼️ Real Profile Pictures:** The plugin automatically fetches high-resolution channel avatars and playlist covers for the main menu.
* **⏯️ "Continue Watching" Support:** Videos are internally declared as episodes. Emby perfectly remembers your exact progress (green progress bar) and displays unfinished videos on your home screen.

### Video Quality \& Playback

* **🎬 4K \& 1080p HLS Muxing:** Automatically muxes the best available adaptive video + audio streams into HLS via FFmpeg. Supports both H.264 and VP9 codecs.
* **⚡ Simultaneous Multi-Quality:** Both 4K and 1080p are muxed together on first play. All quality options appear instantly on the video detail page.
* **🚀 Smart Playback Start:** The plugin waits for the first HLS segments to be ready before returning the stream to Emby — no more "press play twice" errors.
* **📼 Direct MP4 Fallback:** 720p and 480p streams are always available as direct MP4 playback — instant, no muxing required.
* **💾 HLS Cache:** Muxed segments are cached on disk (configurable 0–30 days). Subsequent plays of the same video skip muxing entirely.

### Discover

* **🔥 Trending:** A live "Trending" folder combining Popular, Trending, Music, Gaming, and Movies — all deduplicated and fetched in parallel.
* **🌍 Region Support:** Configure your country code (e.g. `DE`, `US`, `AT`) to get region-specific YouTube trends.

### Shorts / Reels

* **▶ Automatic Detection:** Shorts are detected via Invidious API flags (`isShort`, `genre`), video duration (≤ 3 minutes), and actual video dimensions (vertical aspect ratio).
* **📱 Vertical Playback:** Shorts are served with correct vertical dimensions (e.g. `1080x1920`) so Emby does not stretch them to 16:9. Direct Play is preferred.

### Livestreams

* **🔴 Live Detection:** Videos with `liveNow: true` are automatically marked with a 🔴 LIVE prefix.
* **📡 HLS \& DASH:** Livestreams are played via YouTube's native HLS manifest (or DASH as fallback) with `IsInfiniteStream = true`.

### Sorting

* **📊 Configurable Sort Order:** Sort channel videos by `newest`, `oldest`, or `popular` — configurable in plugin settings.

\---

## 📥 Installation

1. Download the latest `Emby.InvidiousPlugin.dll` from the [Releases](https://github.com/eliasbruno124-dev/Emby-Invidious-Plugin/releases) page.
2. Stop your Emby Server.
3. Place the `.dll` file into your Emby plugins folder:

   * **Path:** `/programdata/plugins`
4. Restart your Emby Server.
5. The plugin will now appear in your Emby dashboard under **Plugins**.

\---

## ⚙️ Configuration

Settings Screenshot

<img width="635" height="847" alt="settings" src="https://github.com/user-attachments/assets/e6d2159c-cccc-470f-aa9d-db0d142ceceb" />

Go to your Emby Dashboard and open the "Invidious" plugin settings.

### Connection

|Setting|Description|
|-|-|
|**Invidious Instance URL**|URL of your self-hosted Invidious instance. Supports Basic Auth: `https://User:Password@invidious.example.com`|

### Content Sources

|Setting|Description|
|-|-|
|**My YouTube Content**|Comma-separated list: `@Handle` for channels, `UCxxxxx` for channel IDs, `PLxxxxx` for playlists, any text for search queries|
|**Watch Later Playlist**|Playlist ID for a ⭐ Watch Later folder with **10-second live refresh**. Add videos on Invidious → they appear in Emby within ~15 seconds. Only the Invidious channel is refreshed — other channels stay untouched.|

*Example:* `@GitHub, PL0lo9MOBetEFcp4SCWinBdpml9B2U25-f, Linux Tutorials`

### Discover

|Setting|Default|Description|
|-|-|-|
|**Show Trending**|✅ On|Show a "Trending" folder with trending and popular videos|
|**Trending Region**|`US`|ISO 3166-1 country code (e.g. `DE`, `US`, `AT`, `CH`, `GB`, `FR`)|

### Sorting

|Setting|Default|Description|
|-|-|-|
|**Sort Channel Videos By**|`newest`|Options: `newest`, `oldest`, `popular`|

### Limits

|Setting|Default|Range|Description|
|-|-|-|-|
|**Max Videos per Channel/Playlist**|50|1–150|Maximum videos loaded per channel or playlist|
|**Max Videos per Search Query**|50|1–150|Maximum videos loaded per search query|

### Quality

|Setting|Default|Description|
|-|-|-|
|**Enable 4K (2160p)**|Off|Enable 4K video quality. When disabled, the maximum HLS quality is 1080p. Disable on hardware-weak systems to save CPU and bandwidth, as 4K VP9 muxing is significantly more demanding.|

### Caching

|Setting|Default|Range|Description|
|-|-|-|-|
|**HLS Cache Duration (Days)**|3|0–30|How long to keep muxed HLS segments on disk. Set to 0 to delete all caches.|

### Advanced / Performance

|Setting|Default|Description|
|-|-|-|
|**FFmpeg Path**|*(auto-detect)*|Custom path to the FFmpeg executable. Leave empty to auto-detect (searches PATH and common install locations).|
|**Pre-Buffer Segments**|90|Max HLS segments to pre-buffer when no one is actively watching. Each segment ≈ 4 seconds (default: ~6 min buffer). Range: 10–300.|

\---

## 🔄 Keeping Content Updated

### Watch Later (Real-Time)

The **Watch Later playlist** is refreshed automatically every **10 seconds** — no configuration needed. When you add or remove a video on Invidious, it appears in Emby within ~15 seconds. Only the Invidious channel is refreshed; other channels are not affected.

### All Other Content (Scheduled Task)

To fetch new videos from your saved channels, playlists, and search queries, Emby uses an automated background task:

1. Go to **Scheduled Tasks** in your Emby Dashboard.
2. Look for the task named **Refresh Internet Channels**.
3. Click on it and add a new **Task Trigger**.
4. Set the interval to run e.g., **every 15 minutes**. This keeps your Emby perfectly in sync with your Invidious playlists!

\---

## 🏗️ Architecture

|File|Purpose|
|-|-|
|`InvidiousChannel.cs`|Main channel provider — folder structure, video listing, media info, Shorts/Live detection|
|`InvidiousApi.cs`|Static HTTP client with retry logic, all Invidious API calls, thumbnail rewriting|
|`MuxHelper.cs`|FFmpeg HLS muxing, process management, segment caching, playback.m3u8 updates|
|`PluginConfiguration.cs`|All user-facing settings with `EditableOptionsBase`|
|`Plugin.cs`|Plugin entry point, Watch Later poller (10s polling + targeted `RefreshChannelContent` via reflection)|
|`thumb.png`|Channel logo|

\---

## 💖 Donate

[![PayPal](https://img.shields.io/badge/PayPal-00457C?style=for-the-badge&logo=paypal&logoColor=white)](https://paypal.me/eliasbruno123)

## 📄 License

This project is open-source and free to use, modify, and distribute.
