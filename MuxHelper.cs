using System;
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
                // 1. Emby config dir (works in Docker: /config/cache/invidious-hls)
                Path.Combine(Environment.GetEnvironmentVariable("XDG_CACHE_HOME") ?? "", "invidious-hls"),
                // 2. Windows LocalAppData
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Emby.InvidiousPlugin", "hls-cache"),
                // 3. /tmp (Docker HOME=/tmp)
                Path.Combine(Path.GetTempPath(), "emby-invidious-hls"),
                // 4. Relative to plugin DLL
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

            // Absolute fallback
            var fb = Path.Combine(Path.GetTempPath(), "emby-invidious-hls");
            try { Directory.CreateDirectory(fb); } catch { }
            return fb;
        }

        private static void Log(string msg)
        {
            try { File.AppendAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {msg}\r\n"); }
            catch { }
        }

        /// <summary>
        /// Starts FFmpeg to mux direct video+audio CDN URLs into HLS segments.
        /// Returns the m3u8 path as soon as first segments are ready (~5 sec).
        /// No auth needed — URLs already contain all tokens.
        /// </summary>
        public static async Task<string?> MuxToHlsAsync(
            string directVideoUrl, string directAudioUrl,
            string videoId, int height)
        {
            Log($"--- MuxToHlsAsync: id={videoId} height={height}");
            Log($"VideoURL length: {directVideoUrl.Length}");
            Log($"AudioURL length: {directAudioUrl.Length}");

            try
            {
                var videoDir = Path.Combine(CacheDir, $"{videoId}_{height}p");
                var m3u8 = Path.Combine(videoDir, "stream.m3u8");

                // Reuse if recent
                if (File.Exists(m3u8))
                {
                    var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(m3u8);
                    if (age.TotalDays < 3)
                    {
                        Log("Cache hit");
                        return m3u8;
                    }
                    try { Directory.Delete(videoDir, true); } catch { }
                }

                Directory.CreateDirectory(videoDir);

                var ffmpeg = FindFfmpeg();
                Log($"FFmpeg: {ffmpeg}");

                var segPattern = Path.Combine(videoDir, "seg_%04d.ts");

                // FFmpeg args explained:
                // -ss 0                      = start from the very beginning (force)
                // -fflags +genpts+discardcorrupt = regenerate timestamps, discard corrupt frames
                // -c:v copy -c:a copy        = no re-encoding
                // -bsf:v h264_mp4toannexb    = convert H.264 to Annex B format (required for .ts)
                // -hls_time 6                = 6-second segments
                // -hls_list_size 0           = keep all segments in playlist
                // -hls_flags append_list     = update playlist as segments arrive
                // -start_number 0            = first segment is 0
                var args = $"-y -ss 0 " +
                           $"-i \"{directVideoUrl}\" -i \"{directAudioUrl}\" " +
                           $"-fflags +genpts+discardcorrupt " +
                           $"-c:v copy -c:a copy " +
                           $"-bsf:v h264_mp4toannexb " +
                           $"-f hls -hls_time 6 -hls_list_size 0 -hls_flags append_list -start_number 0 " +
                           $"-hls_segment_filename \"{segPattern}\" \"{m3u8}\"";

                Log($"Starting FFmpeg...");
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
                Log($"FFmpeg PID={process.Id}");

                // Log ALL stderr (skip noisy progress lines)
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
                        Log($"FFmpeg finished for {videoId}: exit={process.ExitCode}");
                        // Append #EXT-X-ENDLIST to signal "playlist is complete"
                        // Without this, Emby keeps waiting for more segments forever
                        try
                        {
                            if (File.Exists(m3u8))
                            {
                                var content = File.ReadAllText(m3u8);
                                if (!content.Contains("#EXT-X-ENDLIST"))
                                {
                                    File.AppendAllText(m3u8, "\n#EXT-X-ENDLIST\n");
                                    Log("Appended #EXT-X-ENDLIST to playlist");
                                }
                            }
                        }
                        catch { }
                    }
                });

                // Wait until m3u8 has at least 2 segments (playable)
                for (int i = 0; i < 120; i++) // max 60 seconds
                {
                    await Task.Delay(500).ConfigureAwait(false);

                    if (process.HasExited && process.ExitCode != 0)
                    {
                        Log($"FAIL: FFmpeg exit {process.ExitCode}");
                        return null;
                    }

                    if (File.Exists(m3u8))
                    {
                        try
                        {
                            var content = File.ReadAllText(m3u8);
                            int segs = 0;
                            foreach (var line in content.Split('\n'))
                                if (line.TrimEnd().EndsWith(".ts")) segs++;
                            if (segs >= 6)
                            {
                                Log($"SUCCESS: {segs} segments ready → {m3u8}");
                                return m3u8;
                            }
                        }
                        catch { }
                    }
                }

                Log("FAIL: Timeout 60s");
                try { process.Kill(); } catch { }
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
            paths.Add("/bin/ffmpeg");                      // Docker emby/embyserver
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
                    if ((DateTime.UtcNow - Directory.GetLastWriteTimeUtc(d)).TotalDays > 3)
                        try { Directory.Delete(d, true); } catch { }
                }
            }
            catch { }
        }
    }
}
