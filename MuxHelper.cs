using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
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

        // Coordination: only one watchdog per video may perform stop + equalization
        private static readonly ConcurrentDictionary<string, bool> StopInProgress = new();

        // Prevents duplicate resume starts from the session monitor
        private static readonly ConcurrentDictionary<string, bool> ResumeInProgress = new();

        // Cooldown: tracks when a resume last failed (503, connection error, etc.)
        // so the monitor doesn't retry immediately.
        private static readonly ConcurrentDictionary<string, DateTime> ResumeCooldowns = new();
        private const int ResumeCooldownMs = 60_000; // wait 60s before retrying resume

        // Tracks last time a playback file was accessed (by Emby reading the stream)
        // Used as heartbeat for session detection since Emby doesn't populate
        // NowPlayingItem for channel-based HLS streams.
        private static readonly ConcurrentDictionary<string, DateTime> LastAccessTimestamps = new();

        /// <summary>
        /// Records a "heartbeat" for a video — called whenever Emby requests the
        /// cached stream path or when playback.m3u8 is updated.
        /// </summary>
        private static void TouchAccess(string videoId)
        {
            LastAccessTimestamps[videoId] = DateTime.UtcNow;
        }

        /// <summary>
        /// Returns true if a heartbeat for this video was recorded within
        /// the given number of milliseconds.
        /// </summary>
        private static bool HasRecentAccess(string videoId, int withinMs)
        {
            if (LastAccessTimestamps.TryGetValue(videoId, out var ts))
                return (DateTime.UtcNow - ts).TotalMilliseconds < withinMs;
            return false;
        }

        private static readonly object LogLock = new();
        private static CancellationTokenSource? _monitorCts;

        private const int SessionCheckMs = 2000;   // check session every 2s

        /// <summary>
        /// Checks whether any Emby session is currently playing this video.
        /// </summary>
        private static bool IsVideoBeingPlayed(string videoId)
        {
            try
            {
                var sm = Plugin.SessionManager;
                if (sm == null) return false;

                foreach (var session in sm.Sessions)
                {
                    // 1) NowPlayingItem.Path (works for direct/HLS with filesystem paths)
                    var item = session.NowPlayingItem;
                    if (item != null)
                    {
                        var path = item.Path ?? "";
                        if (path.Contains(videoId, StringComparison.OrdinalIgnoreCase))
                            return true;
                    }

                    // 2) PlayState.MediaSourceId (e.g. "videoId_hls_1080p")
                    var sourceId = session.PlayState?.MediaSourceId ?? "";
                    if (!string.IsNullOrEmpty(sourceId)
                        && sourceId.Contains(videoId, StringComparison.OrdinalIgnoreCase))
                        return true;

                    // 3) FullNowPlayingItem — full BaseItem entity with ExternalId
                    //    For channel items ExternalId = channel item ID = raw videoId
                    try
                    {
                        var fullItem = session.FullNowPlayingItem;
                        if (fullItem != null)
                        {
                            var extId = fullItem.ExternalId;
                            if (!string.IsNullOrEmpty(extId)
                                && extId.Contains(videoId, StringComparison.OrdinalIgnoreCase))
                                return true;

                            var fullPath = fullItem.Path;
                            if (!string.IsNullOrEmpty(fullPath)
                                && fullPath.Contains(videoId, StringComparison.OrdinalIgnoreCase))
                                return true;
                        }
                    }
                    catch { /* FullNowPlayingItem may not exist in all Emby versions */ }
                }
            }
            catch (Exception ex)
            {
                Log($"IsVideoBeingPlayed error: {ex.Message}");
            }
            return false;
        }

        /// <summary>
        /// Logs a diagnostic dump of all active Emby sessions (called once per mux start).
        /// </summary>
        private static void LogSessionDiagnostics(string context)
        {
            try
            {
                var sm = Plugin.SessionManager;
                if (sm == null) { Log($"SessionDiag[{context}]: SessionManager is null"); return; }

                int count = 0;
                foreach (var session in sm.Sessions)
                {
                    count++;
                    var item = session.NowPlayingItem;
                    var ps = session.PlayState;
                    string itemPath = item?.Path ?? "(null)";
                    string itemName = item?.Name ?? "(null)";
                    string srcId = ps?.MediaSourceId ?? "(null)";
                    string isPaused = ps?.IsPaused.ToString() ?? "(null)";

                    string extId = "(n/a)";
                    string fullPath = "(n/a)";
                    try
                    {
                        var fi = session.FullNowPlayingItem;
                        if (fi != null)
                        {
                            extId = fi.ExternalId ?? "(null)";
                            fullPath = fi.Path ?? "(null)";
                        }
                        else extId = "(null-item)";
                    }
                    catch { extId = "(err)"; }

                    Log($"SessionDiag[{context}]: #{count} Client={session.Client} " +
                        $"Item.Path={itemPath} Item.Name={itemName} " +
                        $"MediaSourceId={srcId} Paused={isPaused} " +
                        $"FullItem.ExternalId={extId} FullItem.Path={fullPath}");
                }
                if (count == 0)
                    Log($"SessionDiag[{context}]: No active sessions");
            }
            catch (Exception ex)
            {
                Log($"SessionDiag[{context}] error: {ex.Message}");
            }
        }

        private const int MinSegmentsForPlayback = 1;
        private const int MinSegmentsForCache = 6;
        private const int MinSegmentsForResume = 1;
        private const int SegmentWaitMaxIterations = 300;
        private const int RunningProcessWaitMaxIterations = 120;
        private const int PollIntervalMs = 400;
        private const int PlaybackUpdateIntervalMs = 800;

        private static int GetPreBufferSegments()
        {
            try { var p = Plugin.Instance; if (p != null) return Math.Clamp(p.Options.PreBufferSegments, 10, 300); } catch { }
            return 90;
        }

        private static int GetSessionGraceMs()
        {
            try { var p = Plugin.Instance; if (p != null) return Math.Clamp(p.Options.SessionGraceSeconds, 5, 120) * 1000; } catch { }
            return 15_000;
        }

        private static int GetIdleTimeoutMs(bool isVp9 = false)
        {
            // VP9/4K needs much longer for seeking and demuxing
            int baseTimeout;
            try { var p = Plugin.Instance; if (p != null) baseTimeout = Math.Clamp(p.Options.IdleTimeoutSeconds, 15, 300) * 1000; else baseTimeout = 30_000; } catch { baseTimeout = 30_000; }
            return isVp9 ? Math.Max(baseTimeout, 120_000) : baseTimeout;
        }

        static MuxHelper()
        {
            CacheDir = FindWritableDir();
            LogPath = Path.Combine(CacheDir, "_debug.log");
            Initialize();
        }

        /// <summary>
        /// Startup logic. Called from the static constructor and can be invoked
        /// again via EnsureInitialized() on plugin reload.
        /// </summary>
        private static void Initialize()
        {
            Log("=== MuxHelper initialized ===");
            Log($"CacheDir: {CacheDir}");
            Log($"FFmpeg: {FindFfmpeg()}");

            // Clear static dictionaries (needed on plugin reload within the same AppDomain)
            RunningProcesses.Clear();
            StopInProgress.Clear();
            ResumeInProgress.Clear();

            // Kill orphaned FFmpeg processes left over from the previous Emby run
            KillOrphanedFfmpegProcesses();

            MarkOrphanedCachesForResume();
            CleanOldDirs();

            // Stop the old monitor if still active (plugin reload)
            try { _monitorCts?.Cancel(); } catch { }
            try { _monitorCts?.Dispose(); } catch { }
            _monitorCts = new CancellationTokenSource();
            StartSessionResumeMonitor(_monitorCts.Token);
        }

        /// <summary>
        /// Kills all FFmpeg processes that use our cache directory.
        /// Needed because on Windows, child processes survive parent termination.
        /// </summary>
        private static void KillOrphanedFfmpegProcesses()
        {
            try
            {
                int killed = 0;

                foreach (var proc in Process.GetProcessesByName("ffmpeg"))
                {
                    try
                    {
                        // Kill all ffmpeg processes that started BEFORE the current
                        // Emby process (= leftovers from the last run).
                        try
                        {
                            if (proc.StartTime < Process.GetCurrentProcess().StartTime)
                            {
                                proc.Kill();
                                killed++;
                                Log($"KillOrphanedFfmpeg: Killed PID {proc.Id} (started {proc.StartTime:HH:mm:ss})");
                            }
                        }
                        catch { }
                    }
                    catch { }
                    finally { try { proc.Dispose(); } catch { } }
                }

                if (killed > 0)
                    Log($"KillOrphanedFfmpeg: Killed {killed} orphaned FFmpeg process(es)");
            }
            catch (Exception ex)
            {
                Log($"KillOrphanedFfmpeg error: {ex.Message}");
            }
        }

        /// <summary>
        /// On startup: cache dirs without ENDLIST and without idle_stopped marker
        /// belong to FFmpeg processes that were still running during the last shutdown.
        /// Write idle_stopped marker so resume works on next playback.
        /// </summary>
        private static void MarkOrphanedCachesForResume()
        {
            try
            {
                if (!Directory.Exists(CacheDir)) return;

                int marked = 0;
                foreach (var dir in Directory.GetDirectories(CacheDir))
                {
                    var m3u8 = Path.Combine(dir, "stream.m3u8");
                    var idleMarker = Path.Combine(dir, "idle_stopped");
                    var playbackFile = Path.Combine(dir, "playback.m3u8");
                    var metaFile = Path.Combine(dir, "_resume_meta.txt");

                    if (!File.Exists(m3u8)) continue;
                    if (File.Exists(idleMarker)) continue; // already marked

                    try
                    {
                        var content = SafeReadAllText(m3u8);
                        if (content == null) continue;
                        if (content.Contains("#EXT-X-ENDLIST")) continue; // fully muxed

                        int segs = CountSegments(content);
                        if (segs < MinSegmentsForResume) continue; // too few segments to resume

                        // Only mark if resume metadata is available
                        if (!File.Exists(metaFile))
                        {
                            Log($"MarkOrphanedCaches: {Path.GetFileName(dir)} has no _resume_meta.txt, skipping");
                            continue;
                        }

                        File.WriteAllText(idleMarker, $"{DateTime.UtcNow:O}\n{segs}");
                        UpdatePlaybackM3u8(m3u8, playbackFile);

                        Log($"MarkOrphanedCaches: {Path.GetFileName(dir)} marked for resume ({segs} segs)");
                        marked++;
                    }
                    catch { }
                }
                if (marked > 0)
                    Log($"MarkOrphanedCaches: Marked {marked} cache(s) for resume");
            }
            catch (Exception ex)
            {
                Log($"MarkOrphanedCaches error: {ex.Message}");
            }
        }

        private static void Log(string msg)
        {
            try
            {
                lock (LogLock)
                {
                    if (File.Exists(LogPath) && new FileInfo(LogPath).Length > MaxLogBytes)
                    {
                        var oldLog = LogPath + ".old";
                        try { File.Move(LogPath, oldLog, overwrite: true); }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"[MuxHelper] Log rotation failed: {ex.Message}");
                        }
                    }
                    File.AppendAllText(LogPath,
                        $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [T{Environment.CurrentManagedThreadId}] {msg}\r\n");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MuxHelper] Log write failed: {ex.Message}");
            }
        }

        public static void KillAll()
        {
            foreach (var key in RunningProcesses.Keys)
            {
                if (RunningProcesses.TryRemove(key, out var proc))
                {
                    try
                    {
                        if (!proc.HasExited) proc.Kill();
                    }
                    catch { }
                    finally { try { proc.Dispose(); } catch { } }
                }
            }
            Log("Killed all FFmpeg processes");
        }

        public static void Shutdown()
        {
            Log("=== MuxHelper shutting down ===");

            // Stop the monitor
            try { _monitorCts?.Cancel(); } catch { }

            // Kill all FFmpeg processes
            KillAll();

            // Clean up CTS
            try { _monitorCts?.Dispose(); _monitorCts = null; } catch { }

            // Clear static dictionaries so no ghost entries remain
            // on plugin reload
            StopInProgress.Clear();
            ResumeInProgress.Clear();

            // Write idle_stopped for all cache dirs without ENDLIST
            MarkOrphanedCachesForResume();

            Log("=== MuxHelper shutdown complete ===");
        }

        private static string? SafeReadAllText(string path)
        {
            for (int i = 0; i < 3; i++)
            {
                try { return File.ReadAllText(path); }
                catch (IOException) when (i < 2) { Thread.Sleep(50 * (i + 1)); }
            }
            return null;
        }

        private static void SafeWriteAllText(string path, string content)
        {
            for (int i = 0; i < 3; i++)
            {
                try { File.WriteAllText(path, content); return; }
                catch (IOException) when (i < 2) { Thread.Sleep(100 * (i + 1)); }
            }
        }

        private static string? ExtractQueryParam(string url, string param)
        {
            var key = param + "=";
            var idx = url.IndexOf(key, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return null;
            var start = idx + key.Length;
            var end = url.IndexOf('&', start);
            return end > 0 ? url.Substring(start, end - start) : url.Substring(start);
        }

        public static string? GetCachedStreamPath(string videoId, int height)
        {
            var videoDir = Path.Combine(CacheDir, $"{videoId}_{height}p");
            var m3u8 = Path.Combine(videoDir, "stream.m3u8");
            var playback = Path.Combine(videoDir, "playback.m3u8");
            var idleMarker = Path.Combine(videoDir, "idle_stopped");
            try
            {
                if (!File.Exists(m3u8))
                {
                    Log($"GetCachedStreamPath: {videoId}_{height}p — no stream.m3u8");
                    return null;
                }
                var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(m3u8);
                if (age.TotalDays > GetCacheDays())
                {
                    Log($"GetCachedStreamPath: {videoId}_{height}p — cache expired ({age.TotalDays:F1}d)");
                    return null;
                }

                // Partial cache (idle-stopped) → return null
                // so MuxToHlsAsync runs the resume logic
                if (File.Exists(idleMarker))
                {
                    Log($"GetCachedStreamPath: {videoId}_{height}p — idle_stopped marker found → returning null for resume");
                    return null;
                }

                var content = File.ReadAllText(m3u8);
                bool hasEndlist = content.Contains("#EXT-X-ENDLIST");
                int segs = CountSegments(content);
                if (hasEndlist && segs >= MinSegmentsForCache)
                {
                    var result = File.Exists(playback) ? playback : m3u8;
                    Log($"GetCachedStreamPath: {videoId}_{height}p — complete cache ({segs} segs) → {Path.GetFileName(result)}");
                    TouchAccess(videoId);
                    return result;
                }
                Log($"GetCachedStreamPath: {videoId}_{height}p — incomplete (endlist={hasEndlist}, segs={segs})");
            }
            catch (Exception ex)
            {
                Log($"GetCachedStreamPath error for {videoId}_{height}p: {ex.Message}");
            }

            var processKey = $"{videoId}_{height}p";
            if (RunningProcesses.ContainsKey(processKey) && File.Exists(playback))
            {
                try
                {
                    var content = File.ReadAllText(playback);
                    int segs = CountSegments(content);
                    if (segs >= MinSegmentsForPlayback)
                    {
                        Log($"GetCachedStreamPath: {videoId}_{height}p — running process, {segs} segs ready");
                        TouchAccess(videoId);
                        return playback;
                    }
                }
                catch (Exception ex)
                {
                    Log($"GetCachedStreamPath running-check error for {processKey}: {ex.Message}");
                }
            }

            Log($"GetCachedStreamPath: {videoId}_{height}p — returning null");
            return null;
        }

        /// <summary>
        /// Prepares playback.m3u8 for resume: removes ENDLIST so that
        /// Emby treats the file as a growing stream.
        /// </summary>
        public static void PrepareForResume(string videoId, int height)
        {
            var videoDir = Path.Combine(CacheDir, $"{videoId}_{height}p");
            var playback = Path.Combine(videoDir, "playback.m3u8");
            try
            {
                if (!File.Exists(playback)) return;
                var content = File.ReadAllText(playback);
                if (content.Contains("#EXT-X-ENDLIST"))
                {
                    content = content.Replace("#EXT-X-ENDLIST", "").TrimEnd() + "\n";
                    File.WriteAllText(playback, content);
                    Log($"PrepareForResume: Removed ENDLIST from {videoId}_{height}p playback.m3u8");
                }
            }
            catch (Exception ex)
            {
                Log($"PrepareForResume error: {ex.Message}");
            }
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
                catch (Exception ex)
                {
                    Log($"PreparePlaybackPath write error: {ex.Message}");
                }
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
                var content = SafeReadAllText(sourcePath);
                if (content == null) return;

                // Strip DISCONTINUITY tags — Emby's HLS player stops/hangs at these boundaries
                content = StripDiscontinuityTags(content);

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

        // ────────────────────────────────────────────────────────────
        //  Duration-based equalization: the faster quality is stopped
        //  immediately. The slower one continues muxing alone until it
        //  reaches the same DURATION (not segment count!), then it is
        //  also stopped.
        //  → VP9 and H264 have different segment lengths (3-4s vs 5.5s)
        //    → Segment-count comparison leads to unequal content duration.
        // ────────────────────────────────────────────────────────────
        private static async Task EqualizeBeforeStop(string videoId, string triggeringKey)
        {
            // Find all running processes for this video
            // SafeReadAllText prevents race conditions with FFmpeg's m3u8.tmp rename
            var siblings = new List<(string key, double dur, int segs)>();
            foreach (var kvp in RunningProcesses)
            {
                if (!kvp.Key.StartsWith(videoId + "_", StringComparison.Ordinal))
                    continue;

                var dir = Path.Combine(CacheDir, kvp.Key);
                var m3u8 = Path.Combine(dir, "stream.m3u8");
                double dur = 0;
                int segs = 0;
                var content = SafeReadAllText(m3u8);
                if (content != null)
                {
                    dur = ParseTotalDuration(content);
                    segs = CountSegments(content);
                }
                siblings.Add((kvp.Key, dur, segs));
            }

            if (siblings.Count < 2)
            {
                Log($"Equalize: Only 1 quality running for {videoId}, skipping");
                return;
            }

            double maxDur = siblings.Max(s => s.dur);
            // Don't stop the triggering quality immediately — its watchdog is still
            // running and must complete the equalization
            var ahead = siblings
                .Where(s => s.dur >= maxDur && s.key != triggeringKey)
                .ToList();
            // Include the triggering quality if it is behind
            var behind = siblings
                .Where(s => s.dur < maxDur)
                .ToList();

            // ── Stop faster quality/qualities immediately ──
            foreach (var (key, dur, segs) in ahead)
            {
                Log($"Equalize: Stopping faster quality {key} ({dur:F1}s, {segs} segs)");
                StopAndMarkIdle(key);
            }

            if (behind.Count == 0)
            {
                Log($"Equalize: All {siblings.Count} qualities at {maxDur:F1}s, nothing more to do");
                return;
            }

            // ── Let slower quality/qualities keep muxing until they catch up ──
            foreach (var (key, dur, segs) in behind)
                Log($"Equalize: {key} has {dur:F1}s/{maxDur:F1}s ({segs} segs) — waiting until caught up");

            const int pollMs = 2000;
            const int maxWaitMs = 300_000; // 5 minutes max
            var pendingKeys = new HashSet<string>(behind.Select(b => b.key));
            // Stall detection: track last known duration and when it last changed
            var lastDur = new Dictionary<string, double>();
            var lastDurChange = new Dictionary<string, DateTime>();
            foreach (var (key, dur, _) in behind)
            {
                lastDur[key] = dur;
                lastDurChange[key] = DateTime.UtcNow;
            }
            int elapsed = 0;

            while (pendingKeys.Count > 0 && elapsed < maxWaitMs)
            {
                await Task.Delay(pollMs).ConfigureAwait(false);
                elapsed += pollMs;

                foreach (var key in pendingKeys.ToList())
                {
                    if (!RunningProcesses.TryGetValue(key, out var proc))
                    {
                        Log($"Equalize: {key} process ended during catch-up (removed from RunningProcesses)");
                        pendingKeys.Remove(key);
                        continue;
                    }
                    try
                    {
                        if (proc.HasExited)
                        {
                            Log($"Equalize: {key} FFmpeg exited during catch-up (exit={proc.ExitCode})");
                            pendingKeys.Remove(key);
                            continue;
                        }
                    }
                    catch
                    {
                        Log($"Equalize: {key} process state unknown (exception), removing");
                        pendingKeys.Remove(key);
                        continue;
                    }

                    var dir = Path.Combine(CacheDir, key);
                    var m3u8 = Path.Combine(dir, "stream.m3u8");
                    double currentDur = 0;
                    int currentSegs = 0;
                    var content = SafeReadAllText(m3u8);
                    if (content != null)
                    {
                        currentDur = ParseTotalDuration(content);
                        currentSegs = CountSegments(content);
                    }

                    if (currentDur >= maxDur)
                    {
                        if (key == triggeringKey)
                        {
                            Log($"Equalize: {key} (trigger) reached {currentDur:F1}s/{maxDur:F1}s ({currentSegs} segs) — caught up");
                        }
                        else
                        {
                            Log($"Equalize: {key} reached {currentDur:F1}s/{maxDur:F1}s ({currentSegs} segs) — stopping now");
                            StopAndMarkIdle(key);
                        }
                        pendingKeys.Remove(key);
                        continue;
                    }

                    // Stall detection: if duration hasn't grown, check for stall timeout
                    if (currentDur > lastDur.GetValueOrDefault(key, 0))
                    {
                        lastDur[key] = currentDur;
                        lastDurChange[key] = DateTime.UtcNow;
                    }
                    else
                    {
                        bool isVp9 = key.Contains("2160p") || key.Contains("1440p");
                        int stallTimeoutMs = GetIdleTimeoutMs(isVp9);
                        double stalledFor = (DateTime.UtcNow - lastDurChange.GetValueOrDefault(key, DateTime.UtcNow)).TotalMilliseconds;
                        if (stalledFor >= stallTimeoutMs)
                        {
                            Log($"Equalize: {key} STALLED at {currentDur:F1}s/{maxDur:F1}s ({currentSegs} segs) — no progress for {stalledFor / 1000:F0}s, force-stopping");
                            StopAndMarkIdle(key);
                            pendingKeys.Remove(key);
                        }
                    }
                }
            }

            if (pendingKeys.Count > 0)
            {
                Log($"Equalize: Timeout after {elapsed / 1000}s, stopping remaining qualities");
                foreach (var key in pendingKeys)
                {
                    StopAndMarkIdle(key);
                }
            }

            Log($"Equalize: All qualities for {videoId} equalized");
        }

        /// <summary>
        /// Stops a single FFmpeg process and writes the idle_stopped marker.
        /// Waits for process exit to avoid file-lock conflicts with stream.m3u8.
        /// </summary>
        private static void StopAndMarkIdle(string processKey)
        {
            if (!RunningProcesses.TryRemove(processKey, out var proc))
                return;

            try { if (!proc.HasExited) proc.Kill(); }
            catch { }

            // Wait for the process to fully exit and release file handles
            try { proc.WaitForExit(3000); }
            catch { }

            var dir = Path.Combine(CacheDir, processKey);
            var idleMarker = Path.Combine(dir, "idle_stopped");

            try
            {
                var m3u8 = Path.Combine(dir, "stream.m3u8");
                var playbackPath = Path.Combine(dir, "playback.m3u8");

                int segs = 0;
                var content = SafeReadAllText(m3u8);
                if (content != null)
                    segs = CountSegments(content);

                // Write idle marker with retry to handle residual file locks
                SafeWriteAllText(idleMarker, $"{DateTime.UtcNow:O}\n{segs}");

                UpdatePlaybackM3u8(m3u8, playbackPath);

                Log($"StopAndMarkIdle: {processKey} stopped ({segs} segs)");
            }
            catch (Exception ex)
            {
                Log($"StopAndMarkIdle error for {processKey}: {ex.Message}");

                // Last resort: write idle marker even if everything else failed,
                // so the session monitor can still resume this cache later
                try
                {
                    SafeWriteAllText(idleMarker, $"{DateTime.UtcNow:O}\n0");
                    Log($"StopAndMarkIdle: {processKey} — wrote idle marker despite error");
                }
                catch { }
            }

            try { proc.Dispose(); } catch { }
        }

        private static int CountSegments(string content)
        {
            int count = 0;
            foreach (var line in content.Split('\n'))
            {
                var t = line.Trim();
                if (t.EndsWith(".ts", StringComparison.Ordinal)
                    || t.EndsWith(".m4s", StringComparison.Ordinal))
                    count++;
            }
            return count;
        }

        /// <summary>
        /// Removes all #EXT-X-DISCONTINUITY lines from an HLS playlist.
        /// FFmpeg inserts these when resuming with append_list + seek,
        /// but Emby's player stops/hangs at discontinuity boundaries.
        /// </summary>
        private static string StripDiscontinuityTags(string content)
        {
            if (!content.Contains("#EXT-X-DISCONTINUITY"))
                return content;

            var sb = new System.Text.StringBuilder(content.Length);
            foreach (var line in content.Split('\n'))
            {
                if (line.TrimStart().StartsWith("#EXT-X-DISCONTINUITY", StringComparison.Ordinal))
                    continue;
                sb.Append(line).Append('\n');
            }
            return sb.ToString();
        }

        private static int ParseHeightFromProcessKey(string processKey)
        {
            var lastUnderscore = processKey.LastIndexOf('_');
            if (lastUnderscore < 0 || lastUnderscore + 2 >= processKey.Length)
                return 0;

            var suffix = processKey.Substring(lastUnderscore + 1);
            if (!suffix.EndsWith("p", StringComparison.OrdinalIgnoreCase))
                return 0;

            var numberPart = suffix.Substring(0, suffix.Length - 1);
            return int.TryParse(numberPart, out var height) ? height : 0;
        }

        private static bool IsHighestQualityForVideo(string videoId, string processKey)
        {
            var currentHeight = ParseHeightFromProcessKey(processKey);
            var maxHeight = 0;

            foreach (var key in RunningProcesses.Keys)
            {
                if (!key.StartsWith(videoId + "_", StringComparison.Ordinal))
                    continue;

                var h = ParseHeightFromProcessKey(key);
                if (h > maxHeight) maxHeight = h;
            }

            if (maxHeight <= 0) return true;
            return currentHeight >= maxHeight;
        }

        // ────────────────────────────────────────────────────────────
        //  Session-Resume-Monitor: periodically checks whether a session
        //  is playing a video that has an idle_stopped cache.
        //  If so → start FFmpeg resume.
        // ────────────────────────────────────────────────────────────
        private static void StartSessionResumeMonitor(CancellationToken ct)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(5000, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { return; }

                Log("SessionResumeMonitor: Started");

                while (!ct.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(3000, ct).ConfigureAwait(false);
                        CheckForResumeOpportunities();
                    }
                    catch (OperationCanceledException) { break; }
                    catch (Exception ex)
                    {
                        Log($"SessionResumeMonitor error: {ex.Message}");
                    }
                }
                Log("SessionResumeMonitor: Stopped");
            }, ct);
        }

        private static void CheckForResumeOpportunities()
        {
            var sm = Plugin.SessionManager;
            if (sm == null) return;

            try
            {
                if (!Directory.Exists(CacheDir)) return;

                foreach (var dir in Directory.GetDirectories(CacheDir))
                {
                    var idleMarker = Path.Combine(dir, "idle_stopped");
                    if (!File.Exists(idleMarker)) continue;

                    var dirName = Path.GetFileName(dir);
                    if (string.IsNullOrEmpty(dirName)) continue;

                    // processKey = "videoId_heightp"
                    var lastUnderscore = dirName.LastIndexOf('_');
                    if (lastUnderscore < 0) continue;
                    var videoId = dirName.Substring(0, lastUnderscore);

                    // Already running a process for this video?
                    if (RunningProcesses.ContainsKey(dirName)) continue;
                    // Already being resumed?
                    if (ResumeInProgress.ContainsKey(dirName)) continue;

                    // Cooldown: skip if last resume attempt failed recently
                    if (ResumeCooldowns.TryGetValue(dirName, out var cooldownUntil)
                        && (DateTime.UtcNow - cooldownUntil).TotalMilliseconds < ResumeCooldownMs)
                        continue;

                    // Is this video currently being played?
                    if (!HasRecentAccess(videoId, GetSessionGraceMs()) && !IsVideoBeingPlayed(videoId)) continue;

                    // Read resume metadata
                    var metaFile = Path.Combine(dir, "_resume_meta.txt");
                    if (!File.Exists(metaFile)) continue;

                    if (!ResumeInProgress.TryAdd(dirName, true)) continue;

                    try
                    {
                        var lines = File.ReadAllLines(metaFile);
                        if (lines.Length < 4)
                        {
                            ResumeInProgress.TryRemove(dirName, out _);
                            continue;
                        }

                        int height = int.TryParse(lines[2], NumberStyles.Integer,
                            CultureInfo.InvariantCulture, out var h) ? h : 0;
                        bool isVp9 = bool.TryParse(lines[3], out var v) && v;

                        if (height <= 0)
                        {
                            ResumeInProgress.TryRemove(dirName, out _);
                            continue;
                        }

                        // Reconstruct URLs from current config (robust against URL changes)
                        string? authHeader = null;
                        string videoUrl, audioUrl;
                        try
                        {
                            var cfgUrl = Plugin.Instance?.Options?.InvidiousUrl;
                            if (string.IsNullOrEmpty(cfgUrl))
                            {
                                Log($"SessionResumeMonitor: No Invidious URL configured, skipping {dirName}");
                                ResumeInProgress.TryRemove(dirName, out _);
                                continue;
                            }

                            var cleanBase = InvidiousApi.GetCleanBaseUrl(cfgUrl);
                            var hdrs = InvidiousApi.BuildPlaybackHeaders(cfgUrl);
                            hdrs.TryGetValue("Authorization", out authHeader);

                            if (lines[0].StartsWith("http", StringComparison.OrdinalIgnoreCase))
                            {
                                // Legacy format: full URLs
                                videoUrl = lines[0];
                                audioUrl = lines[1];
                            }
                            else
                            {
                                // New format: itags → frische URLs bauen
                                videoUrl = $"{cleanBase}/latest_version?id={videoId}&itag={lines[0]}&local=true";
                                audioUrl = $"{cleanBase}/latest_version?id={videoId}&itag={lines[1]}&local=true";
                            }
                        }
                        catch (Exception ex)
                        {
                            Log($"SessionResumeMonitor: Config error for {dirName}: {ex.Message}");
                            ResumeInProgress.TryRemove(dirName, out _);
                            continue;
                        }

                        Log($"SessionResumeMonitor: Resuming {dirName} (session active)");

                        // Remove ENDLIST from playback.m3u8
                        PrepareForResume(videoId, height);

                        // Start FFmpeg resume in background
                        var capturedVideoUrl = videoUrl;
                        var capturedAudioUrl = audioUrl;
                        var capturedHeight = height;
                        var capturedIsVp9 = isVp9;
                        var capturedAuth = authHeader;
                        var capturedVideoId = videoId;
                        var capturedDirName = dirName;

                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                await MuxToHlsAsync(capturedVideoUrl, capturedAudioUrl,
                                    capturedVideoId, capturedHeight, capturedIsVp9,
                                    capturedAuth).ConfigureAwait(false);
                            }
                            catch (Exception ex)
                            {
                                Log($"SessionResumeMonitor: Resume failed for {capturedDirName}: {ex.Message}");
                                ResumeCooldowns[capturedDirName] = DateTime.UtcNow;
                            }
                            finally
                            {
                                ResumeInProgress.TryRemove(capturedDirName, out _);
                            }
                        });
                    }
                    catch (Exception ex)
                    {
                        Log($"SessionResumeMonitor: Error for {dirName}: {ex.Message}");
                        ResumeInProgress.TryRemove(dirName, out _);
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"CheckForResumeOpportunities error: {ex.Message}");
            }
        }

        public static async Task<string?> MuxToHlsAsync(
            string directVideoUrl, string directAudioUrl,
            string videoId, int height, bool isVp9 = false,
            string? authHeader = null,
            CancellationToken ct = default)
        {
            var processKey = $"{videoId}_{height}p";
            Log($"--- MuxToHlsAsync: {processKey} (codec={(isVp9 ? "vp9" : "h264")})");
            LogSessionDiagnostics(processKey);

            try
            {
                var videoDir = Path.Combine(CacheDir, processKey);
                var m3u8 = Path.Combine(videoDir, "stream.m3u8");
                var playback = GetPlaybackPath(videoDir);

                if (RunningProcesses.ContainsKey(processKey))
                {
                    Log($"FFmpeg already running for {processKey}, waiting...");
                    for (int i = 0; i < RunningProcessWaitMaxIterations; i++)
                    {
                        ct.ThrowIfCancellationRequested();
                        await Task.Delay(PollIntervalMs, ct).ConfigureAwait(false);

                        if (!RunningProcesses.ContainsKey(processKey))
                        {
                            Log($"Running process for {processKey} completed");
                            break;
                        }

                        if (File.Exists(playback))
                        {
                            try
                            {
                                var c = File.ReadAllText(playback);
                                int segs = CountSegments(c);
                                if (segs >= MinSegmentsForPlayback)
                                {
                                    Log($"Ready: {segs} segments in {processKey}");
                                    return playback;
                                }
                            }
                            catch (Exception ex)
                            {
                                Log($"Segment check error for {processKey}: {ex.Message}");
                            }
                        }
                    }
                    // After wait timeout: process finished, check if data is available
                    if (File.Exists(playback))
                    {
                        try
                        {
                            var fc = File.ReadAllText(playback);
                            if (CountSegments(fc) >= 1)
                            {
                                Log($"Process completed, returning {CountSegments(fc)} segments");
                                return playback;
                            }
                        }
                        catch { }
                    }
                    Log("Wait timeout for running process, no playback available");
                    return null;
                }

                if (RunningProcesses.TryRemove(processKey, out var oldProc))
                {
                    try
                    {
                        if (!oldProc.HasExited)
                        {
                            oldProc.Kill();
                            Log($"Killed stale FFmpeg for {processKey}");
                        }
                    }
                    catch (Exception ex)
                    {
                        Log($"Stale process cleanup error: {ex.Message}");
                    }
                    finally { try { oldProc.Dispose(); } catch { } }
                }

                bool resumeAttempted = false;
                if (File.Exists(m3u8))
                {
                    var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(m3u8);
                    if (age.TotalDays < GetCacheDays())
                    {
                        var cached = File.ReadAllText(m3u8);

                        // Complete cache → return immediately
                        if (cached.Contains("#EXT-X-ENDLIST")
                            && CountSegments(cached) >= MinSegmentsForCache)
                        {
                            Log($"Cache hit ({CountSegments(cached)} segments)");
                            UpdatePlaybackM3u8(m3u8, playback);
                            return File.Exists(playback) ? playback : m3u8;
                        }

                        // Partial cache (idle-stopped) → prepare for resume
                        var idleMarker = Path.Combine(videoDir, "idle_stopped");
                        if (File.Exists(idleMarker) && CountSegments(cached) >= MinSegmentsForResume)
                        {
                            Log($"RESUME: Found partial cache ({CountSegments(cached)} segs) for {processKey}");
                            resumeAttempted = true;
                            // Don't delete — will be resumed below with -ss
                        }
                        else
                        {
                            Log($"Cache stale ({CountSegments(cached)} segs), re-muxing from scratch");
                            try { Directory.Delete(videoDir, true); }
                            catch (Exception ex)
                            {
                                Log($"Cache dir cleanup error: {ex.Message}");
                            }
                        }
                    }
                    else
                    {
                        try { Directory.Delete(videoDir, true); }
                        catch (Exception ex)
                        {
                            Log($"Cache dir cleanup error: {ex.Message}");
                        }
                    }
                }

                Directory.CreateDirectory(videoDir);

                try
                {
                    var ffmpeg = FindFfmpeg();

                    string segExt = isVp9 ? "m4s" : "ts";

                    // ── Resume detection ──
                    bool isResume = false;
                    string seekArg = "";
                    var idleMarkerPath = Path.Combine(videoDir, "idle_stopped");

                    if (File.Exists(idleMarkerPath) && File.Exists(m3u8) && resumeAttempted)
                    {
                        try
                        {
                            var existingContent = File.ReadAllText(m3u8);
                            int existingSegs = CountSegments(existingContent);
                            double existingDuration = ParseTotalDuration(existingContent);

                            if (existingSegs >= MinSegmentsForResume)
                            {
                                isResume = true;
                                // Resume at the exact last position (no overlap).
                                // FFmpeg input-seek jumps to the nearest keyframe before,
                                // append_list continues the timeline seamlessly.
                                double seekSec = existingDuration;
                                seekArg = $"-ss {seekSec.ToString("F2", CultureInfo.InvariantCulture)} ";
                                Log($"RESUME: {existingSegs} segs cached ({existingDuration.ToString("F1", CultureInfo.InvariantCulture)}s), seeking to {seekSec.ToString("F1", CultureInfo.InvariantCulture)}s");

                                // Delete idle marker
                                try { File.Delete(idleMarkerPath); } catch { }

                                // Remove #EXT-X-ENDLIST so Emby keeps polling.
                                // Do NOT insert DISCONTINUITY — it breaks Emby's seeking.
                                // append_list continues segment numbering automatically.
                                if (existingContent.Contains("#EXT-X-ENDLIST"))
                                {
                                    existingContent = existingContent.Replace("#EXT-X-ENDLIST", "").TrimEnd();
                                }
                                existingContent += "\n";
                                File.WriteAllText(m3u8, existingContent);
                            }
                            else
                            {
                                Log($"Resume: insufficient data (segs={existingSegs}, duration={existingDuration}), starting fresh");
                                try { File.Delete(idleMarkerPath); } catch { }
                                try { Directory.Delete(videoDir, true); } catch { }
                                Directory.CreateDirectory(videoDir);
                            }
                        }
                        catch (Exception ex)
                        {
                            Log($"Resume detection error: {ex.Message}, starting fresh");
                            try { File.Delete(idleMarkerPath); } catch { }
                            try { Directory.Delete(videoDir, true); } catch { }
                            Directory.CreateDirectory(videoDir);
                        }
                    }
                    else if (!File.Exists(idleMarkerPath) && resumeAttempted)
                    {
                        // Marker missing but resumeAttempted is true — cleanup needed
                        Log($"Resume marker missing, re-muxing from scratch");
                        try { Directory.Delete(videoDir, true); } catch { }
                        Directory.CreateDirectory(videoDir);
                    }

                    var segPattern = Path.Combine(videoDir, $"seg_%04d.{segExt}");

                    string codecArgs = isVp9
                        ? "-c:v copy -c:a copy -hls_segment_type fmp4"
                        : "-c:v copy -c:a copy -bsf:v h264_mp4toannexb";

                    // ── Speed optimizations ──
                    // -headers: Basic Auth for Invidious proxy
                    // -thread_queue_size 4096: larger input buffer per stream
                    // -fflags +nobuffer: no internal buffering delay
                    // -flags low_delay: minimal latency
                    // -hls_init_time 1: first segment ready in ~1s
                    // -hls_time 4: subsequent segments ~4s each
                    string headersArg = !string.IsNullOrEmpty(authHeader)
                        ? $"-headers \"Authorization: {authHeader}\r\n\" "
                        : "";

                    // On resume: skip -avoid_negative_ts make_zero so PTS
                    // from input-seek is preserved and the HLS timeline
                    // continues seamlessly. Also skip -start_number since
                    // append_list continues segment numbering from the
                    // existing m3u8 automatically.
                    string avoidNegTs = isResume ? "" : "-avoid_negative_ts make_zero ";

                    var args =
                        $"-y " +
                        $"-probesize 1M -analyzeduration 2M " +
                        $"{seekArg}" +
                        $"-reconnect 1 -reconnect_streamed 1 " +
                        $"-reconnect_delay_max 30 -reconnect_on_network_error 1 " +
                        $"-thread_queue_size 4096 " +
                        $"{headersArg}" +
                        $"-i \"{directVideoUrl}\" " +
                        $"{seekArg}" +
                        $"-reconnect 1 -reconnect_streamed 1 " +
                        $"-reconnect_delay_max 30 -reconnect_on_network_error 1 " +
                        $"-thread_queue_size 4096 " +
                        $"{headersArg}" +
                        $"-i \"{directAudioUrl}\" " +
                        $"-map 0:v:0 -map 1:a:0 " +
                        $"-fflags +genpts+discardcorrupt " +
                        $"{avoidNegTs}" +
                        $"-max_muxing_queue_size 4096 " +
                        $"{codecArgs} " +
                        $"-f hls -hls_init_time 1 -hls_time 4 " +
                        $"-hls_list_size 0 -hls_flags append_list " +
                        $"-hls_segment_filename \"{segPattern}\" \"{m3u8}\"";

                    // Save resume metadata (itags for URL reconstruction)
                    try
                    {
                        var metaFile = Path.Combine(videoDir, "_resume_meta.txt");
                        var videoItag = ExtractQueryParam(directVideoUrl, "itag") ?? "0";
                        var audioItag = ExtractQueryParam(directAudioUrl, "itag") ?? "0";
                        File.WriteAllLines(metaFile, new[]
                        {
                            videoItag,
                            audioItag,
                            height.ToString(CultureInfo.InvariantCulture),
                            isVp9.ToString(),
                        });
                    }
                    catch (Exception ex) { Log($"Save resume meta error: {ex.Message}"); }

                    Log($"Starting FFmpeg: {ffmpeg} [resume={isResume}]");
                    Log($"FFmpeg args: {args}");
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
                                    if (!string.IsNullOrEmpty(t)
                                        && !t.StartsWith("frame=", StringComparison.Ordinal))
                                        Log($"FFmpeg[{processKey}]: {t}");
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Log($"FFmpeg stderr reader error for {processKey}: {ex.Message}");
                        }
                        finally
                        {
                            int exitCode = -1;
                            try { exitCode = process.ExitCode; }
                            catch (Exception ex)
                            {
                                Log($"ExitCode read error for {processKey}: {ex.Message}");
                            }
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
                                catch (Exception ex)
                                {
                                    Log($"Post-mux error for {processKey}: {ex.Message}");
                                }
                            }
                            else if (exitCode == 137 || exitCode == 9)
                            {
                                // Process was killed intentionally (SIGKILL=137/9)
                                // StopAndMarkIdle already handled the idle marker.
                                Log($"FFmpeg killed for {processKey}: exit={exitCode} (intentional stop)");
                            }
                            else
                            {
                                // FFmpeg crashed (I/O error, connection drop, 503, etc.)
                                // Remove the #EXT-X-ENDLIST that FFmpeg wrote so the cache
                                // is not mistaken for a complete mux. Write idle_stopped
                                // marker so resume logic picks it up on next playback.
                                try
                                {
                                    if (File.Exists(m3u8))
                                    {
                                        var content = File.ReadAllText(m3u8);
                                        int segs = CountSegments(content);
                                        if (content.Contains("#EXT-X-ENDLIST"))
                                        {
                                            content = content.Replace("#EXT-X-ENDLIST", "").TrimEnd() + "\n";
                                            File.WriteAllText(m3u8, content);
                                            Log($"FFmpeg error exit: removed ENDLIST from {processKey} ({segs} segs)");
                                        }
                                        var idleMarker = Path.Combine(videoDir, "idle_stopped");
                                        if (!File.Exists(idleMarker))
                                        {
                                            SafeWriteAllText(idleMarker, $"{DateTime.UtcNow:O}\n{segs}");
                                            Log($"FFmpeg error exit: marked {processKey} for resume ({segs} segs)");
                                        }
                                        UpdatePlaybackM3u8(m3u8, playback);
                                    }
                                    // Set cooldown so resume monitor waits before retrying
                                    ResumeCooldowns[processKey] = DateTime.UtcNow;
                                    Log($"FFmpeg error exit: cooldown set for {processKey} ({ResumeCooldownMs / 1000}s)");
                                }
                                catch (Exception ex)
                                {
                                    Log($"FFmpeg error-exit handler failed for {processKey}: {ex.Message}");
                                }
                            }

                            RunningProcesses.TryRemove(processKey, out _);
                            try { process.Dispose(); } catch { }
                        }
                    });

                    _ = Task.Run(async () =>
                    {
                        int lastSegCount = 0;
                        DateTime lastNewSegTime = DateTime.UtcNow;
                        DateTime lastSessionSeen = DateTime.UtcNow;
                        DateTime muxStartedAt = DateTime.UtcNow;
                        const int InitialGraceMs = 45_000; // Emby needs 25-40s to populate session info
                        int tickCounter = 0;

                        while (RunningProcesses.ContainsKey(processKey))
                        {
                            UpdatePlaybackM3u8(m3u8, playback);

                            try
                            {
                                int currentSegs = 0;
                                if (File.Exists(m3u8))
                                {
                                    var watchdogContent = SafeReadAllText(m3u8);
                                    if (watchdogContent != null) currentSegs = CountSegments(watchdogContent);
                                }

                                if (currentSegs > lastSegCount)
                                {
                                    lastSegCount = currentSegs;
                                    lastNewSegTime = DateTime.UtcNow;
                                }

                                // ── Session check: use HTTP-access heartbeat ──
                                // Emby doesn't populate NowPlayingItem for channel-based
                                // HLS streams, so IsVideoBeingPlayed() always returns false.
                                // Instead we track when GetCachedStreamPath was last called
                                // for this video (= Emby is actively reading the stream).
                                tickCounter++;
                                if ((tickCounter % (SessionCheckMs / PlaybackUpdateIntervalMs)) == 0)
                                {
                                    if (HasRecentAccess(videoId, GetSessionGraceMs()) || IsVideoBeingPlayed(videoId))
                                        lastSessionSeen = DateTime.UtcNow;
                                }

                                // During the initial grace period, assume session is active.
                                // Also seed the access timestamp so the heartbeat chain starts.
                                bool inInitialGrace = (DateTime.UtcNow - muxStartedAt).TotalMilliseconds < InitialGraceMs;
                                if (inInitialGrace)
                                {
                                    lastSessionSeen = DateTime.UtcNow;
                                    TouchAccess(videoId);
                                }

                                bool activeSession = (DateTime.UtcNow - lastSessionSeen).TotalMilliseconds < GetSessionGraceMs();

                                // ── Pre-buffer limit: no viewer → stop after X segments ──
                                if (!activeSession && currentSegs >= GetPreBufferSegments())
                                {
                                    bool processStillRunning = false;
                                    if (RunningProcesses.TryGetValue(processKey, out var pbCheck))
                                    {
                                        try { processStillRunning = !pbCheck.HasExited; }
                                        catch { }
                                    }
                                    if (processStillRunning)
                                    {
                                        if (!IsHighestQualityForVideo(videoId, processKey))
                                        {
                                            await Task.Delay(1000).ConfigureAwait(false);
                                            continue;
                                        }
                                        if (StopInProgress.TryAdd(videoId, true))
                                        {
                                            try
                                            {
                                                Log($"PRE-BUFFER: {processKey} reached {currentSegs} segs (max={GetPreBufferSegments()}), no active viewer — stopping");
                                                LogSessionDiagnostics($"PRE-BUFFER-STOP:{processKey}");
                                                await EqualizeBeforeStop(videoId, processKey).ConfigureAwait(false);
                                                Log($"PRE-BUFFER: Equalization done for {videoId}");
                                                var allKeys = RunningProcesses.Keys
                                                    .Where(k => k.StartsWith(videoId + "_", StringComparison.Ordinal))
                                                    .ToList();
                                                foreach (var key in allKeys)
                                                    StopAndMarkIdle(key);
                                                StopAndMarkIdle(processKey);
                                            }
                                            finally { StopInProgress.TryRemove(videoId, out _); }
                                            break;
                                        }
                                        else
                                        {
                                            while (StopInProgress.ContainsKey(videoId))
                                                await Task.Delay(500).ConfigureAwait(false);
                                            break;
                                        }
                                    }
                                }

                                bool noSession = !activeSession;
                                bool ffmpegStalled = (DateTime.UtcNow - lastNewSegTime).TotalMilliseconds >= GetIdleTimeoutMs(isVp9);

                                if (noSession || ffmpegStalled)
                                {
                                    bool processStillRunning = false;
                                    if (RunningProcesses.TryGetValue(processKey, out var checkProc))
                                    {
                                        try { processStillRunning = !checkProc.HasExited; }
                                        catch { }
                                    }

                                    if (!processStillRunning)
                                        break;

                                    // Only start stop/equalize from the highest quality level,
                                    // so higher qualities are not prematurely stopped separately.
                                    if (!IsHighestQualityForVideo(videoId, processKey))
                                    {
                                        await Task.Delay(1000).ConfigureAwait(false);
                                        continue;
                                    }

                                    // Only one watchdog per video may stop
                                    if (!StopInProgress.TryAdd(videoId, true))
                                    {
                                        // Another watchdog is already stopping → wait
                                        Log($"STOP: Another watchdog already stopping {videoId}, waiting...");
                                        while (StopInProgress.ContainsKey(videoId))
                                            await Task.Delay(500).ConfigureAwait(false);
                                        break;
                                    }

                                    try
                                    {
                                        string reason = noSession ? "no session" : "FFmpeg stalled";
                                        Log($"STOP: {reason} ({currentSegs} segs), equalizing {processKey}");
                                        if (noSession) LogSessionDiagnostics($"STOP:{processKey}");

                                        await EqualizeBeforeStop(videoId, processKey).ConfigureAwait(false);
                                        Log($"STOP: Equalization done for {videoId}");

                                        // Stop ALL qualities for this video (incl. triggering one)
                                        var allKeys = RunningProcesses.Keys
                                            .Where(k => k.StartsWith(videoId + "_", StringComparison.Ordinal))
                                            .ToList();
                                        foreach (var key in allKeys)
                                            StopAndMarkIdle(key);

                                        // Stop the triggering quality itself if still present
                                        StopAndMarkIdle(processKey);
                                    }
                                    finally
                                    {
                                        StopInProgress.TryRemove(videoId, out _);
                                    }
                                    break;
                                }
                            }
                            catch (Exception ex)
                            {
                                Log($"Watchdog error for {processKey}: {ex.Message}");
                            }

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
                        try
                        {
                            hasExited = process.HasExited;
                            exitCode = hasExited ? process.ExitCode : 0;
                        }
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
                                    Log($"SUCCESS: {CountSegments(content)} segments for {processKey}");
                                    return playback;
                                }
                            }
                            catch (Exception ex)
                            {
                                Log($"Segment read error: {ex.Message}");
                            }
                        }
                    }

                    Log($"FAIL: Timeout ({SegmentWaitMaxIterations * PollIntervalMs / 1000}s) for {processKey}");
                    try { process.Kill(); }
                    catch (Exception ex) { Log($"Timeout kill error: {ex.Message}"); }
                    RunningProcesses.TryRemove(processKey, out _);
                    return null;
                }
                catch
                {
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

        private static double ParseTotalDuration(string m3u8Content)
        {
            double total = 0;
            foreach (var line in m3u8Content.Split('\n'))
            {
                var t = line.Trim();
                if (t.StartsWith("#EXTINF:", StringComparison.Ordinal))
                {
                    // Format: #EXTINF:4.000000, or #EXTINF:4.000,
                    var val = t.Substring(8);
                    var commaIdx = val.IndexOf(',');
                    if (commaIdx > 0) val = val.Substring(0, commaIdx);
                    if (double.TryParse(val, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var dur))
                        total += dur;
                }
            }
            return total;
        }

        private static string FindFfmpeg()
        {
            try
            {
                var configPath = Plugin.Instance?.Options?.FfmpegPath;
                if (!string.IsNullOrWhiteSpace(configPath))
                {
                    if (File.Exists(configPath)) return configPath;
                    Log($"Configured FFmpeg path not found: {configPath}");
                }
            }
            catch { }

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
                return !string.IsNullOrEmpty(output) && File.Exists(output)
                    ? output : null;
            }
            catch { return null; }
        }

        private static string FindWritableDir()
        {
            var candidates = new[]
            {
                Path.Combine(
                    Environment.GetEnvironmentVariable("XDG_CACHE_HOME") ?? "",
                    "invidious-hls"),
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Emby.InvidiousPlugin", "hls-cache"),
                Path.Combine(Path.GetTempPath(), "emby-invidious-hls"),
                Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory ?? "",
                    "..", "cache", "invidious-hls"),
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
                if (!Directory.Exists(CacheDir)) return;
                int cacheDays = GetCacheDays();

                // CacheDays=0: kill all running processes and delete all caches
                if (cacheDays <= 0)
                {
                    KillAll();
                    foreach (var dir in Directory.GetDirectories(CacheDir))
                    {
                        try
                        {
                            Directory.Delete(dir, true);
                            Log($"CacheDays=0: Deleted cache {Path.GetFileName(dir)}");
                        }
                        catch (Exception ex)
                        {
                            Log($"CleanOldDirs delete error: {ex.Message}");
                        }
                    }
                    Log("CacheDays=0: All caches cleared");
                    return;
                }

                foreach (var dir in Directory.GetDirectories(CacheDir))
                {
                    if ((DateTime.UtcNow - Directory.GetLastWriteTimeUtc(dir)).TotalDays
                        > cacheDays)
                    {
                        try
                        {
                            Directory.Delete(dir, true);
                            Log($"Deleted old cache: {Path.GetFileName(dir)}");
                        }
                        catch (Exception ex)
                        {
                            Log($"CleanOldDirs delete error: {ex.Message}");
                        }
                    }
                }
            }
            catch (Exception ex) { Log($"CleanOldDirs error: {ex.Message}"); }
        }
    }
}