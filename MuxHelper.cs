using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Emby.InvidiousPlugin
{
    public static class MuxHelper
    {
        private static readonly string CacheDir;
        private static readonly string LogPath;
        private const long MaxLogBytes = 2 * 1024 * 1024;

        private static readonly ConcurrentDictionary<string, Process> RunningProcesses = new();

        private static readonly SemaphoreSlim MuxGate = new(4, 4);

        private const int MinSegmentsForPlayback = 2;
        private const int MinSegmentsForCache = 6;
        private const int SegmentWaitMaxIterations = 240;
        private const int RunningProcessWaitMaxIterations = 120;
        private const int PollIntervalMs = 500;
        private const int PlaybackUpdateIntervalMs = 1000;

        static MuxHelper()
        {
            CacheDir = FindWritableDir();
            LogPath = Path.Combine(CacheDir, "_debug.log");
            Log("=== MuxHelper initialized ===");
            Log($"CacheDir: {CacheDir}");
            Log($"FFmpeg: {FindFfmpeg()}");
            CleanOldDirs();
        }

        private static void Log(string msg)
        {
            try
            {
                if (File.Exists(LogPath) && new FileInfo(LogPath).Length > MaxLogBytes)
                {
                    var oldLog = LogPath + ".old";
                    try { File.Move(LogPath, oldLog, overwrite: true); }
                    catch (Exception ex) { Debug.WriteLine($"[MuxHelper] Log rotation failed: {ex.Message}"); }
                }
                File.AppendAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [T{Environment.CurrentManagedThreadId}] {msg}\r\n");
            }
            catch (Exception ex) { Debug.WriteLine($"[MuxHelper] Log write failed: {ex.Message}"); }
        }

        public static int ActiveMuxCount => RunningProcesses.Count;

        public static void KillProcess(string videoId)
        {
            foreach (var key in RunningProcesses.Keys)
            {
                if (key == videoId || key.StartsWith(videoId + "_", StringComparison.Ordinal))
                {
                    if (RunningProcesses.TryRemove(key, out var proc))
                    {
                        try { if (!proc.HasExited) { proc.Kill(); Log($"Killed FFmpeg for {key}"); } }
                        catch (Exception ex) { Log($"Kill failed for {key}: {ex.Message}"); }
                        finally { try { proc.Dispose(); } catch { } }
                    }
                }
            }
        }

        public static void KillAll()
        {
            foreach (var key in RunningProcesses.Keys)
            {
                if (RunningProcesses.TryRemove(key, out var proc))
                {
                    try { if (!proc.HasExited) proc.Kill(); }
                    catch (Exception ex) { Log($"KillAll failed for {key}: {ex.Message}"); }
                    finally { try { proc.Dispose(); } catch { } }
                }
            }
            Log("Killed all FFmpeg processes");
        }

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
                if (content.Contains("#EXT-X-ENDLIST") && CountSegments(content) >= MinSegmentsForCache)
                    return File.Exists(playback) ? playback : m3u8;
            }
            catch (Exception ex) { Log($"GetCachedStreamPath error for {videoId}_{height}p: {ex.Message}"); }

            var processKey = $"{videoId}_{height}p";
            if (RunningProcesses.ContainsKey(processKey) && File.Exists(playback))
            {
                try
                {
                    var content = File.ReadAllText(playback);
                    if (CountSegments(content) >= MinSegmentsForPlayback)
                        return playback;
                }
                catch (Exception ex) { Log($"GetCachedStreamPath running-check error for {processKey}: {ex.Message}"); }
            }

            return null;
        }

        public static string PreparePlaybackPath(string videoId, int height)
        {
            var videoDir = Path.Combine(CacheDir, $"{videoId}_{height}p");
            try { Directory.CreateDirectory(videoDir); } catch { }
            var playback = Path.Combine(videoDir, "playback.m3u8");
            if (!File.Exists(playback))
            {
                try
                {
                    File.WriteAllText(playback,
                        "#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-TARGETDURATION:6\n#EXT-X-MEDIA-SEQUENCE:0\n");
                }
                catch (Exception ex) { Log($"PreparePlaybackPath write error: {ex.Message}"); }
            }
            return playback;
        }

        private static int GetCacheDays()
        {
            try
            {
                var plugin = Plugin.Instance;
                if (plugin != null) return Math.Max(0, plugin.Options.CacheDays);
            }
            catch (Exception ex) { Log($"GetCacheDays error: {ex.Message}"); }
            return 3;
        }

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

                var tmpPath = playbackPath + ".tmp";
                File.WriteAllText(tmpPath, content);
                try
                {
                    File.Move(tmpPath, playbackPath, overwrite: true);
                }
                catch
                {
                    try { File.Copy(tmpPath, playbackPath, overwrite: true); } catch { }
                    try { File.Delete(tmpPath); } catch { }
                }
            }
            catch (Exception ex) { Log($"UpdatePlaybackM3u8 error: {ex.Message}"); }
        }

        private static int CountSegments(string content)
        {
            int count = 0;
            foreach (var line in content.Split('\n'))
            {
                var t = line.Trim();
                if (t.EndsWith(".ts", StringComparison.Ordinal) || t.EndsWith(".m4s", StringComparison.Ordinal))
                    count++;
            }
            return count;
        }

        public static async Task<string?> MuxToHlsAsync(
            string directVideoUrl, string directAudioUrl,
            string videoId, int height, bool isVp9 = false,
            CancellationToken ct = default)
        {
            var processKey = $"{videoId}_{height}p";
            Log($"--- MuxToHlsAsync: {processKey} (codec={(isVp9 ? "vp9" : "h264")})");

            try
            {
                var videoDir = Path.Combine(CacheDir, processKey);
                var m3u8 = Path.Combine(videoDir, "stream.m3u8");
                var playback = GetPlaybackPath(videoDir);

                if (RunningProcesses.ContainsKey(processKey))
                {
                    Log($"FFmpeg already running for {processKey}, waiting for segments...");
                    for (int i = 0; i < RunningProcessWaitMaxIterations; i++)
                    {
                        ct.ThrowIfCancellationRequested();
                        await Task.Delay(PollIntervalMs, ct).ConfigureAwait(false);

                        if (!RunningProcesses.ContainsKey(processKey)) break;

                        if (File.Exists(playback))
                        {
                            try
                            {
                                var c = File.ReadAllText(playback);
                                int segs = CountSegments(c);
                                if (segs >= MinSegmentsForPlayback)
                                {
                                    Log($"Ready to stream: {segs} segments in {processKey}");
                                    return playback;
                                }
                            }
                            catch (Exception ex) { Log($"Segment check error for {processKey}: {ex.Message}"); }
                        }
                    }
                    if (File.Exists(playback))
                    {
                        var fc = File.ReadAllText(playback);
                        if (CountSegments(fc) >= 1) return playback;
                    }
                    Log("Wait timeout for running process, returning null");
                    return null;
                }

                if (RunningProcesses.TryRemove(processKey, out var oldProc))
                {
                    try { if (!oldProc.HasExited) { oldProc.Kill(); Log($"Killed stale FFmpeg for {processKey}"); } }
                    catch (Exception ex) { Log($"Stale process cleanup error: {ex.Message}"); }
                    finally { try { oldProc.Dispose(); } catch { } }
                }

                if (File.Exists(m3u8))
                {
                    var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(m3u8);
                    if (age.TotalDays < GetCacheDays())
                    {
                        var cached = File.ReadAllText(m3u8);
                        if (cached.Contains("#EXT-X-ENDLIST") && CountSegments(cached) >= MinSegmentsForCache)
                        {
                            Log($"Cache hit ({CountSegments(cached)} segments)");
                            UpdatePlaybackM3u8(m3u8, playback);
                            return File.Exists(playback) ? playback : m3u8;
                        }
                        Log($"Cache stale ({CountSegments(cached)} segs), re-muxing");
                    }
                    try { Directory.Delete(videoDir, true); }
                    catch (Exception ex) { Log($"Cache dir cleanup error: {ex.Message}"); }
                }

                Directory.CreateDirectory(videoDir);

                if (!await MuxGate.WaitAsync(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false))
                {
                    Log($"FAIL: Too many concurrent muxes ({ActiveMuxCount}), rejecting {processKey}");
                    return null;
                }

                bool semaphoreOwnedByCaller = true;
                try
                {
                    var ffmpeg = FindFfmpeg();

                    string segExt = isVp9 ? "m4s" : "ts";
                    var segPattern = Path.Combine(videoDir, $"seg_%04d.{segExt}");

                    string codecArgs = isVp9
                        ? "-c:v copy -c:a copy -hls_segment_type fmp4"
                        : "-c:v copy -c:a copy -bsf:v h264_mp4toannexb";

                    var args = $"-y " +
                               $"-i \"{directVideoUrl}\" " +
                               $"-i \"{directAudioUrl}\" " +
                               $"-map 0:v:0 -map 1:a:0 " +
                               $"-fflags +genpts+discardcorrupt " +
                               $"-avoid_negative_ts make_zero " +
                               $"{codecArgs} " +
                               $"-f hls -hls_time 6 -hls_list_size 0 -hls_flags append_list -start_number 0 " +
                               $"-hls_segment_filename \"{segPattern}\" \"{m3u8}\"";

                    Log($"Starting FFmpeg: {ffmpeg}");
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
                        Log("FAIL: Process.Start returned null");
                        return null;
                    }

                    RunningProcesses[processKey] = process;
                    semaphoreOwnedByCaller = false;
                    Log($"FFmpeg PID={process.Id} for {processKey}");

                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            using var reader = process.StandardError;
                            while (!reader.EndOfStream)
                            {
                                var line = await reader.ReadLineAsync().ConfigureAwait(false);
                                if (line != null)
                                {
                                    var t = line.Trim();
                                    if (!string.IsNullOrEmpty(t) && !t.StartsWith("frame=", StringComparison.Ordinal))
                                        Log($"FFmpeg[{processKey}]: {t}");
                                }
                            }
                        }
                        catch (Exception ex) { Log($"FFmpeg stderr reader error for {processKey}: {ex.Message}"); }
                        finally
                        {
                            int exitCode = -1;
                            try { exitCode = process.ExitCode; }
                            catch (Exception ex) { Log($"ExitCode read error for {processKey}: {ex.Message}"); }
                            Log($"FFmpeg finished for {processKey}: exit={exitCode}");

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
                                        }
                                        UpdatePlaybackM3u8(m3u8, playback);
                                    }
                                }
                                catch (Exception ex) { Log($"Post-mux finalization error for {processKey}: {ex.Message}"); }
                            }

                            RunningProcesses.TryRemove(processKey, out _);
                            MuxGate.Release();
                            try { process.Dispose(); } catch { }
                        }
                    });

                    _ = Task.Run(async () =>
                    {
                        while (RunningProcesses.ContainsKey(processKey))
                        {
                            UpdatePlaybackM3u8(m3u8, playback);
                            await Task.Delay(PlaybackUpdateIntervalMs).ConfigureAwait(false);
                        }
                        UpdatePlaybackM3u8(m3u8, playback);
                    });

                    for (int i = 0; i < SegmentWaitMaxIterations; i++)
                    {
                        ct.ThrowIfCancellationRequested();
                        await Task.Delay(PollIntervalMs, ct).ConfigureAwait(false);

                        bool hasExited;
                        int exitCode;
                        try { hasExited = process.HasExited; exitCode = hasExited ? process.ExitCode : 0; }
                        catch (Exception ex)
                        {
                            Log($"FAIL: Lost FFmpeg process handle: {ex.Message}");
                            RunningProcesses.TryRemove(processKey, out _);
                            return null;
                        }

                        if (hasExited && exitCode != 0)
                        {
                            Log($"FAIL: FFmpeg exited with code {exitCode}");
                            RunningProcesses.TryRemove(processKey, out _);
                            return null;
                        }

                        if (File.Exists(playback))
                        {
                            try
                            {
                                var content = File.ReadAllText(playback);
                                if (CountSegments(content) >= MinSegmentsForPlayback)
                                {
                                    Log($"SUCCESS: {CountSegments(content)} segments ready for {processKey}");
                                    return playback;
                                }
                            }
                            catch (Exception ex) { Log($"Segment read error during wait: {ex.Message}"); }
                        }
                    }

                    Log($"FAIL: Timeout waiting for segments ({SegmentWaitMaxIterations * PollIntervalMs / 1000}s) for {processKey}");
                    try { process.Kill(); } catch (Exception ex) { Log($"Timeout kill error: {ex.Message}"); }
                    RunningProcesses.TryRemove(processKey, out _);
                    return null;
                }
                catch
                {
                    if (semaphoreOwnedByCaller)
                        MuxGate.Release();
                    throw;
                }
            }
            catch (OperationCanceledException)
            {
                Log($"MuxToHlsAsync cancelled for {processKey}");
                return null;
            }
            catch (Exception ex)
            {
                Log($"EXCEPTION in MuxToHlsAsync: {ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }

        private static string FindFfmpeg()
        {
            var fromPath = FindInSystemPath("ffmpeg");
            if (!string.IsNullOrEmpty(fromPath)) return fromPath!;

            var candidates = new List<string>();

            var appBase = AppDomain.CurrentDomain.BaseDirectory ?? "";
            if (!string.IsNullOrEmpty(appBase))
            {
                candidates.Add(Path.Combine(appBase, "ffmpeg.exe"));
                candidates.Add(Path.Combine(appBase, "ffmpeg"));
            }

            candidates.AddRange(new[]
            {
                @"C:\Program Files\Emby-Server\system\ffmpeg.exe",
                @"C:\Program Files\EmbyServer\system\ffmpeg.exe",
                @"C:\ffmpeg\bin\ffmpeg.exe",
                @"C:\ffmpeg\ffmpeg.exe",
                "/opt/emby-server/bin/ffmpeg",
                "/usr/lib/emby-server/bin/ffmpeg",
                "/usr/bin/ffmpeg",
                "/usr/local/bin/ffmpeg",
                "/bin/ffmpeg",
                "/opt/homebrew/bin/ffmpeg",
                "/usr/local/opt/ffmpeg/bin/ffmpeg",
                "/snap/bin/ffmpeg",
            });

            foreach (var path in candidates)
            {
                try { if (File.Exists(path)) return path; }
                catch { }
            }

            return "ffmpeg";
        }

        private static string? FindInSystemPath(string executable)
        {
            try
            {
                bool isWindows = Environment.OSVersion.Platform == PlatformID.Win32NT;
                var psi = new ProcessStartInfo
                {
                    FileName = isWindows ? "where" : "which",
                    Arguments = executable,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                using var proc = Process.Start(psi);
                if (proc == null) return null;
                var output = proc.StandardOutput.ReadLine()?.Trim();
                proc.WaitForExit(3000);
                return !string.IsNullOrEmpty(output) && File.Exists(output) ? output : null;
            }
            catch { return null; }
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
                    var testFile = Path.Combine(full, ".write-test");
                    File.WriteAllText(testFile, "ok");
                    File.Delete(testFile);
                    return full;
                }
                catch { }
            }

            var fallback = Path.Combine(Path.GetTempPath(), "emby-invidious-hls");
            try { Directory.CreateDirectory(fallback); } catch { }
            return fallback;
        }

        private static void CleanOldDirs()
        {
            try
            {
                int cacheDays = GetCacheDays();
                foreach (var dir in Directory.GetDirectories(CacheDir))
                {
                    if ((DateTime.UtcNow - Directory.GetLastWriteTimeUtc(dir)).TotalDays > cacheDays)
                    {
                        try { Directory.Delete(dir, true); Log($"Deleted old cache: {Path.GetFileName(dir)}"); }
                        catch (Exception ex) { Log($"CleanOldDirs delete error: {ex.Message}"); }
                    }
                }
            }
            catch (Exception ex) { Log($"CleanOldDirs error: {ex.Message}"); }
        }
    }
}