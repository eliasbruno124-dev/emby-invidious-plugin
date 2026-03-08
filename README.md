# 📺 Emby Invidious Plugin (Self-Hosted Edition)

Plugin Screenshot <img width="1918" height="867" alt="main site" src="https://github.com/user-attachments/assets/a9bc0d27-f65f-4f8f-b06a-cf026e86c074" /><img width="1918" height="875" alt="overview" src="https://github.com/user-attachments/assets/5c03fd67-263f-48ed-b26d-794fa84c20d5" /><img width="1918" height="867" alt="channel" src="https://github.com/user-attachments/assets/85dc2686-13b6-4efe-9e4f-cc3bd9e24370" />




A powerful, privacy-friendly YouTube plugin for [Emby](https://emby.media/) that operates entirely without Google API keys. It uses an Invidious instance (ideally self-hosted) to seamlessly integrate channels, playlists, and search queries directly into your Emby dashboard.

## ✨ Features

* **🛡️ 100% Privacy:** No Google trackers, no API keys required. Everything runs locally through your Invidious instance.
* **🔄 Seamless Playlist Syncing:** Log into your Invidious account and create a **public playlist**. Add its `PL...` ID to this plugin. Any video you add to that playlist via Invidious will now automatically sync to your Emby dashboard!
* **🎯 Smart "@" Input:** A single, clean text field for all your content! Type `@GitHub` for channels, `PL123...` for playlists, or regular words for search queries.
* **🖼️ Real Profile Pictures:** The plugin automatically fetches high-resolution channel avatars and playlist covers for the main menu.
* **📝 Uncut Descriptions:** Bypasses the standard Invidious limits to asynchronously load complete video descriptions, including proper line breaks and view counts.
* **📅 Accurate Dates:** Extracts the exact, true upload date, bypassing the Invidious "playlist-added-date" bug.
* **⏯️ "Continue Watching" Support:** Videos are internally declared as episodes. Emby perfectly remembers your exact progress (green progress bar) and displays unfinished videos on your home screen.
* **🚀 Direct MP4 Playback:** Silently extracts the pure MP4 stream in the background. This guarantees instant, error-free playback on *any* Smart TV, smartphone, or browser.

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

Settings Screenshot <img width="1913" height="868" alt="plugin" src="https://github.com/user-attachments/assets/d4ad03fa-a2e2-4dfe-99d2-5810c7aa1c05" /><img width="1918" height="867" alt="plugin settings" src="https://github.com/user-attachments/assets/43b2364a-8966-494f-acd3-431850dcc35b" />


Go to your Emby Dashboard and open the "Invidious" plugin settings.

1. **My Invidious Instance URL:** Enter the URL of your self-hosted Invidious instance (e.g., `http://localhost:3000`).
2. **Max Videos:** Set the maximum number of videos you want to load per channel or search query.
3. **My YouTube Content:** Simply separate your entries with a comma:
   * **Channel:** Start with an `@` (e.g., `@GitHub`).
   * **Playlist:** Enter the playlist ID starting with `PL` (e.g., `PL0lo9MOBetEFcp...`).
   * **Search:** Just type regular search terms (e.g., `Minecraft Trailer`).

*Example input:* `@GitHub, PL0lo9MOBetEFcp4SCWinBdpml9B2U25-f, Linux Tutorials`

---

## 🔄 Keeping Content Updated (Scheduled Tasks)

To fetch new videos from your saved channels and playlists, Emby uses an automated background task. Here is how to set it up perfectly:

1. Go to **Scheduled Tasks** in your Emby Dashboard.
2. Look for the task named **Refresh Internet Channels**.
3. Click on it and add a new **Task Trigger**.
4. Set the interval to run e.g., **every 15 minutes**. This keeps your Emby perfectly in sync with your Invidious playlists!

---

## 🛠️ For Developers (Compiling)
1. Clone this repository.
2. Open the project in Visual Studio.
3. Ensure you have the required Emby Server references added (`MediaBrowser.Common`, `MediaBrowser.Controller`, `MediaBrowser.Model`).
4. Build the project as a Class Library.

## 💖 Donate
If you find this plugin useful and want to support its development, feel free to leave a tip. Thank you! 💙

[![PayPal](https://img.shields.io/badge/PayPal-00457C?style=for-the-badge&logo=paypal&logoColor=white)](https://paypal.me/eliasbruno123)

## 📄 License
This project is open-source and free to use, modify, and distribute.
