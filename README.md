# 📺 Emby Invidious Plugin (Self-Hosted Edition)

A powerful, privacy-friendly YouTube plugin for [Emby](https://emby.media/) that operates entirely without Google API keys. It uses an Invidious instance (ideally self-hosted) to seamlessly integrate channels, playlists, and search queries directly into your Emby dashboard.

## ✨ Features

* **🛡️ 100% Privacy:** No Google trackers, no API keys required. Everything runs locally through your Invidious instance.
* **🔄 Seamless Playlist Syncing:** Log into your Invidious account and create a **public playlist**. Add its `PL...` ID to this plugin. Any video you add to that playlist via Invidious will automatically sync to your Emby dashboard!
* **🎯 Smart "@" Input:** A single, clean text field for all your content! Type `@GitHub` for channels, `PL123...` for playlists, or regular words for search queries.
* **🖼️ Real Profile Pictures:** The plugin automatically fetches high-resolution channel avatars and playlist covers for the main menu.
* **📝 Uncut Descriptions:** Bypasses the standard Invidious limits to asynchronously load complete video descriptions, including proper line breaks and view counts.
---

## 📥 Installation

1. Download the latest `Emby.InvidiousPlugin.dll` from the Releases page.
2. Stop your Emby Server.
3. Place the `.dll` file directly into your Emby plugins folder:
   * **Path:** `/programdata/plugins`
4. Restart your Emby Server.
5. The plugin will now appear in your Emby dashboard under **Plugins**.

---

## ⚙️ Configuration

Go to your Emby Dashboard and open the "Invidious" plugin settings.

1. **My Invidious Instance URL:** Enter the URL of your self-hosted Invidious instance (e.g., `http://localhost:3000`).
2. **Max Videos:** Set the maximum number of videos you want to load per channel or search query.
3. **My YouTube Content:** Simply separate your entries with a comma:
   * **Channel:** Start with an `@` (e.g., `@GitHub`).
   * **Playlist:** Enter the playlist ID starting with `PL` (e.g., `PL0lo9MOBetEFcp...`).
   * **Search:** Just type regular search terms (e.g., `Minecraft Trailer`).

*Example input:* `@GitHub, PL0lo9MOBetEFcp4SCWinBdpml9B2U25-f, Linux Tutorials`

---

## 🛠️ For Developers (Compiling)
1. Clone this repository.
2. Open the project in Visual Studio.
3. Ensure you have the required Emby Server references added (`MediaBrowser.Common`, `MediaBrowser.Controller`, `MediaBrowser.Model`).
4. Build the project as a Class Library.

## 📄 License
This project is open-source and free to use, modify, and distribute.
