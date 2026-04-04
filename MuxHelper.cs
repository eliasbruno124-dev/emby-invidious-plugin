using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace Emby.InvidiousPlugin
{
    public static class MuxHelper
    {
        private static readonly string CacheDir;
        private static readonly string LogPath;

        // Track running FFmpeg processes so we can kill them
        private static readonly ConcurrentDictionary<string, Process> RunningProcesses = new();

        static MuxHelper()
        {
            CacheDir = FindWritableDir();
            LogPath = Path.Combine(CacheDir, "_debug.log");
            Log("=== MuxHelper initialized ===");
            Log($"CacheDir: {CacheDir}");
            CleanOldDirs();
        }

        private static string FindWritableDir()
        {
            var candidates = new[]
            {
                Path.Combine(Environment.GetEnvironmentVariable("XDG_CACHE_HOME") ?? "", "invidious-hls"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Emby.InvidiousPlugin", "hls-cache"),
                Path.Combine(Path.GetTempPath(), "emby-invidious-hls"),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory ?? "", "..", "cache", "invidious-hls"),
            };

            foreach (var dir in candidates)
            {
                if (string.IsNullOrWhiteSpace(dir)) continue;
                try
                {
                    var full = Path.GetFullPath(dir);
                    Directory.CreateDirectory(full);
                    var test = Path.Combine(full, ".test");
                    File.WriteAllText(test, "ok");
                    File.Delete(test);
                    return full;
                }
                catch { }
            }

            var fb = Path.Combine(Path.GetTempPath(), "emby-invidious-hls");
            try { Directory.CreateDirectory(fb); } catch { }
            return fb;
        }

        private static void Log(string msg)
        {
            try { File.AppendAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {msg}\r\n"); }
            catch { }
        }

        public static void KillProcess(string videoId)
        {
            foreach (var kvp in RunningProcesses)
            {
                if (kvp.Key.StartsWith(videoId + "_") || kvp.Key == videoId)
                {
                    try { if (!kvp.Value.HasExited) { kvp.Value.Kill(); Log($"Killed FFmpeg for {kvp.Key}"); } }
                    catch { }
                    RunningProcesses.TryRemove(kvp.Key, out _);
                }
            }
        }

        /// <summary>
        /// Kill FFmpeg processes for all videos EXCEPT the given one.
        /// Called when user starts playing a new video.
        /// </summary>
        public static void KillOtherVideos(string currentVideoId)
        {
            foreach (var kvp in RunningProcesses)
            {
                if (!kvp.Key.StartsWith(currentVideoId + "_"))
                {
                    try { if (!kvp.Value.HasExited) { kvp.Value.Kill(); Log($"Killed FFmpeg for other video: {kvp.Key}"); } }
                    catch { }
                    RunningProcesses.TryRemove(kvp.Key, out _);
                }
            }
        }

        public static void KillAll()
        {
            foreach (var kvp in RunningProcesses)
            {
                try { if (!kvp.Value.HasExited) kvp.Value.Kill(); } catch { }
            }
            RunningProcesses.Clear();
            Log("Killed all FFmpeg processes");
        }

        /// <summary>
        /// Returns the cached stream.m3u8 path if complete, null otherwise.
        /// </summary>
        public static string? GetCachedStreamPath(string videoId, int height)
        {
            var videoDir = Path.Combine(CacheDir, $"{videoId}_{height}p");
            var m3u8 = Path.Combine(videoDir, "stream.m3u8");
            var playback = Path.Combine(videoDir, "playback.m3u8");
            try
            {
                if (!File.Exists(m3u8)) return null;
                var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(m3u8);
                if (age.TotalDays > GetCacheDays()) return null;
                var content = File.ReadAllText(m3u8);
                if (content.Contains("#EXT-X-ENDLIST") && CountSegments(content) >= 6)
                    return File.Exists(playback) ? playback : m3u8;
            }
            catch { }

            // Also check if FFmpeg is currently running for this
            var processKey = $"{videoId}_{height}p";
            if (RunningProcesses.ContainsKey(processKey) && File.Exists(playback))
                return playback;

            return null;
        }

        /// <summary>
        /// Returns the expected playback.m3u8 path for a given video+height
        /// without starting any mux. Used to list sources before muxing.
        /// Also ensures the directory exists.
        /// </summary>
        public static string GetExpectedPlaybackPath(string videoId, int height)
        {
            var videoDir = Path.Combine(CacheDir, $"{videoId}_{height}p");
            try { Directory.CreateDirectory(videoDir); } catch { }
            var playback = Path.Combine(videoDir, "playback.m3u8");
            // Create minimal stub so Emby accepts the path before mux completes
            if (!File.Exists(playback))
            {
                try { File.WriteAllText(playback, "#EXTM3U\n#EXT-X-TARGETDURATION:6\n#EXT-X-ENDLIST\n"); }
                catch { }
            }
            return playback;
        }

        /// <summary>
        /// Gets cache duration in days from plugin settings.
        /// </summary>
        private static int GetCacheDays()
        {
            try
            {
                var plugin = Plugin.Instance;
                if (plugin != null) return Math.Max(0, plugin.Options.CacheDays);
            }
            catch { }
            return 3;
        }

        /// <summary>
        /// Creates a "playback" copy of the m3u8 that ALWAYS has #EXT-X-ENDLIST.
        /// This makes Emby treat it as VOD (starts from beginning) instead of
        /// live (jumps to end).
        /// </summary>
        private static string GetPlaybackPath(string videoDir) =>
            Path.Combine(videoDir, "playback.m3u8");

        private static void UpdatePlaybackM3u8(string sourcePath, string playbackPath)
        {
            try
            {
                if (!File.Exists(sourcePath)) return;
                var content = File.ReadAllText(sourcePath);
                if (!content.Contains("#EXT-X-ENDLIST"))
                    content += "\n#EXT-X-ENDLIST\n";
                // Atomic write: write to temp file, then rename
                // This prevents Emby from reading a half-written playlist
                // Write to temp first, then copy over (atomic-ish on Windows)
                var tmpPath = playbackPath + ".tmp";
                File.WriteAllText(tmpPath, content);
                File.Copy(tmpPath, playbackPath, true);
                try { File.Delete(tmpPath); } catch { }
            }
            catch { }
        }

        private static int CountSegments(string content)
        {
            int segs = 0;
            foreach (var line in content.Split('\n'))
            {
                var trimmed = line.TrimEnd();
                if (trimmed.EndsWith(".ts") || trimmed.EndsWith(".m4s")) segs++;
            }
            return segs;
        }

        /// <summary>
        /// Starts FFmpeg to mux direct video+audio CDN URLs into HLS segments.
        /// Returns a playback.m3u8 path that ALWAYS contains #EXT-X-ENDLIST
        /// so Emby treats it as VOD and starts from the beginning.
        /// A background thread keeps updating this file as FFmpeg produces more segments.
        /// </summary>
        public static async Task<string?> MuxToHlsAsync(
            string directVideoUrl, string directAudioUrl,
            string videoId, int height, bool isVp9 = false)
        {
            var processKey = $"{videoId}_{height}p";
            Log($"--- MuxToHlsAsync: {processKey} (codec={( isVp9 ? "vp9" : "h264" )})");
            Log($"VideoURL length: {directVideoUrl.Length}");
            Log($"AudioURL length: {directAudioUrl.Length}");

            try
            {
                var videoDir = Path.Combine(CacheDir, processKey);
                var m3u8 = Path.Combine(videoDir, "stream.m3u8");       // FFmpeg writes here
                var playback = GetPlaybackPath(videoDir);                // Emby reads this

                // If FFmpeg is already running for this video, return existing playback.m3u8
                if (RunningProcesses.ContainsKey(processKey) && File.Exists(playback))
                {
                    Log("Reusing running FFmpeg, returning playback.m3u8");
                    return playback;
                }

                // Kill only if same exact resolution is already running (e.g. retry)
                // Don't kill other heights — they run in parallel
                if (RunningProcesses.TryRemove(processKey, out var oldProc))
                {
                    try { if (!oldProc.HasExited) { oldProc.Kill(); Log($"Killed old FFmpeg for {processKey}"); } }
                    catch { }
                }

                // Reuse completed cache
                if (File.Exists(m3u8))
                {
                    var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(m3u8);
                    if (age.TotalDays < GetCacheDays())
                    {
                        var cached = File.ReadAllText(m3u8);
                        if (cached.Contains("#EXT-X-ENDLIST") && CountSegments(cached) >= 6)
                        {
                            // For completed playlists, stream.m3u8 already has ENDLIST
                            Log($"Cache hit (complete, {CountSegments(cached)} segments)");
                            return m3u8;
                        }
                        var hasEndList = cached.Contains("#EXT-X-ENDLIST");
                        Log($"Cache invalid ({CountSegments(cached)} segs, ENDLIST={hasEndList}), re-muxing");
                    }
                    try { Directory.Delete(videoDir, true); } catch { }
                }

                Directory.CreateDirectory(videoDir);

                var ffmpeg = FindFfmpeg();
                Log($"FFmpeg: {ffmpeg}");

                // VP9 needs fMP4 segments (.m4s), H264 uses MPEG-TS (.ts)
                string segExt = isVp9 ? "m4s" : "ts";
                var segPattern = Path.Combine(videoDir, $"seg_%04d.{segExt}");

                string codecArgs = isVp9
                    ? "-c:v copy -c:a copy -hls_segment_type fmp4"                    // VP9 in fMP4
                    : "-c:v copy -c:a copy -bsf:v h264_mp4toannexb";                  // H264 in MPEG-TS

                var args = $"-y " +
                           $"-i \"{directVideoUrl}\" " +
                           $"-i \"{directAudioUrl}\" " +
                           $"-map 0:v:0 -map 1:a:0 " +
                           $"-fflags +genpts+discardcorrupt " +
                           $"-avoid_negative_ts make_zero " +
                           $"{codecArgs} " +
                           $"-f hls -hls_time 6 -hls_list_size 0 -hls_flags append_list -start_number 0 " +
                           $"-hls_segment_filename \"{segPattern}\" \"{m3u8}\"";

                Log("Starting FFmpeg...");
                var psi = new ProcessStartInfo
                {
                    FileName = ffmpeg,
                    Arguments = args,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                var process = Process.Start(psi);
                if (process == null)
                {
                    Log("FAIL: Process null");
                    return null;
                }

                RunningProcesses[processKey] = process;
                Log($"FFmpeg PID={process.Id}");

                // Background: log stderr, finalize when done
                _ = Task.Run(async () =>
                {
                    try
                    {
                        using var reader = process.StandardError;
                        while (!reader.EndOfStream)
                        {
                            var line = await reader.ReadLineAsync();
                            if (line != null)
                            {
                                var t = line.Trim();
                                if (!t.StartsWith("elapsed=") && !string.IsNullOrEmpty(t))
                                    Log($"FFmpeg: {t}");
                            }
                        }
                    }
                    catch { }
                    finally
                    {
                        var exitCode = -1;
                        try { exitCode = process.ExitCode; } catch { }
                        Log($"FFmpeg finished for {processKey}: exit={exitCode}");

                        // Only finalize stream.m3u8 on successful exit
                        if (exitCode == 0)
                        {
                            try
                            {
                                if (File.Exists(m3u8))
                                {
                                    var content = File.ReadAllText(m3u8);
                                    if (!content.Contains("#EXT-X-ENDLIST"))
                                    {
                                        content += "\n#EXT-X-ENDLIST\n";
                                        File.WriteAllText(m3u8, content);
                                        Log("Appended #EXT-X-ENDLIST to stream.m3u8");
                                    }
                                    // Final update of playback.m3u8
                                    UpdatePlaybackM3u8(m3u8, playback);
                                }
                            }
                            catch { }
                        }

                        RunningProcesses.TryRemove(processKey, out _);
                        try { process.Dispose(); } catch { }
                    }
                });

                // Background: keep playback.m3u8 updated with ENDLIST every second
                _ = Task.Run(async () =>
                {
                    Log("Playback updater started");
                    while (RunningProcesses.ContainsKey(processKey))
                    {
                        UpdatePlaybackM3u8(m3u8, playback);
                        await Task.Delay(1000).ConfigureAwait(false);
                    }
                    // One final update after process removed
                    UpdatePlaybackM3u8(m3u8, playback);
                    Log("Playback updater stopped");
                });

                // Wait until playback.m3u8 has enough segments
                for (int i = 0; i < 240; i++) // max 120 seconds (4K VP9 needs more time)
                {
                    await Task.Delay(500).ConfigureAwait(false);

                    try
                    {
                        if (process.HasExited && process.ExitCode != 0)
                        {
                            Log($"FAIL: FFmpeg exit {process.ExitCode}");
                            RunningProcesses.TryRemove(processKey, out _);
                            return null;
                        }
                    }
                    catch
                    {
                        Log("FAIL: FFmpeg process lost");
                        RunningProcesses.TryRemove(processKey, out _);
                        return null;
                    }

                    if (File.Exists(playback))
                    {
                        try
                        {
                            var content = File.ReadAllText(playback);
                            int segs = CountSegments(content);
                            if (segs >= 6)
                            {
                                Log($"SUCCESS: {segs} segments ready in playback.m3u8");
                                return playback;
                            }
                        }
                        catch { }
                    }
                }

                Log("FAIL: Timeout 120s");
                try { process.Kill(); } catch { }
                RunningProcesses.TryRemove(processKey, out _);
                return null;
            }
            catch (Exception ex)
            {
                Log($"EXCEPTION: {ex.Message}");
                return null;
            }
        }

        private static string FindFfmpeg()
        {
            var paths = new List<string>();
            var appBase = AppDomain.CurrentDomain.BaseDirectory ?? "";
            if (!string.IsNullOrEmpty(appBase))
            {
                paths.Add(Path.Combine(appBase, "ffmpeg.exe"));
                paths.Add(Path.Combine(appBase, "ffmpeg"));
            }
            paths.Add(@"C:\Program Files\Emby-Server\system\ffmpeg.exe");
            paths.Add(@"C:\ffmpeg\ffmpeg.exe");
            paths.Add("/bin/ffmpeg");
            paths.Add("/opt/emby-server/bin/ffmpeg");
            paths.Add("/usr/lib/emby-server/bin/ffmpeg");
            paths.Add("/usr/bin/ffmpeg");
            paths.Add("/usr/local/bin/ffmpeg");
            foreach (var p in paths)
                try { if (File.Exists(p)) return p; } catch { }
            return "ffmpeg";
        }

        private static void CleanOldDirs()
        {
            try
            {
                foreach (var d in Directory.GetDirectories(CacheDir))
                {
                    if ((DateTime.UtcNow - Directory.GetLastWriteTimeUtc(d)).TotalDays > GetCacheDays())
                        try { Directory.Delete(d, true); } catch { }
                }
            }
            catch { }
        }
    }
}
