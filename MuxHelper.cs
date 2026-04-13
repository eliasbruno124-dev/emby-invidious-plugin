using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
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

        // Dynamisch konfigurierbar — 0 = unlimited
        private static SemaphoreSlim MuxGate = new(4, 4);
        private static int _currentMuxLimit = 4;
        private static readonly object MuxGateLock = new();

        private static SemaphoreSlim GetMuxGate()
        {
            int configured;
            try
            {
                var plugin = Plugin.Instance;
                configured = plugin != null ? plugin.Options.MaxConcurrentMuxes : 4;
            }
            catch { configured = 4; }

            // 0 = unlimited → wir setzen auf 64 als praktisches Maximum
            if (configured <= 0) configured = 64;

            lock (MuxGateLock)
            {
                if (configured != _currentMuxLimit)
                {
                    Log($"MuxGate limit changed: {_currentMuxLimit} → {configured}");
                    MuxGate = new SemaphoreSlim(configured, configured);
                    _currentMuxLimit = configured;
                }
                return MuxGate;
            }
        }

        private const int MinSegmentsForPlayback = 4;   // war 2 → mehr Buffer
        private const int MinSegmentsForCache = 6;
        private const int SegmentWaitMaxIterations = 300; // war 240 → mehr Geduld
        private const int RunningProcessWaitMaxIterations = 120;
        private const int PollIntervalMs = 400;           // war 500 → schnellere Reaktion
        private const int PlaybackUpdateIntervalMs = 800;  // war 1000 → häufigere Updates
        private const int IdleTimeoutMs = 60_000;          // 60s ohne Segment-Zugriff → FFmpeg stoppen

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
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[MuxHelper] Log rotation failed: {ex.Message}");
                    }
                }
                File.AppendAllText(LogPath,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [T{Environment.CurrentManagedThreadId}] {msg}\r\n");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MuxHelper] Log write failed: {ex.Message}");
            }
        }

        public static int ActiveMuxCount => RunningProcesses.Count;

        public static void KillProcess(string videoId)
        {
            foreach (var key in RunningProcesses.Keys)
            {
                if (key == videoId
                    || key.StartsWith(videoId + "_", StringComparison.Ordinal))
                {
                    if (RunningProcesses.TryRemove(key, out var proc))
                    {
                        try
                        {
                            if (!proc.HasExited)
                            {
                                proc.Kill();
                                Log($"Killed FFmpeg for {key}");
                            }
                        }
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

                // Partieller Cache (idle-gestoppt) → null zurückgeben
                // damit MuxToHlsAsync die Resume-Logik ausführt
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
        /// Bereitet playback.m3u8 für Resume vor: entfernt ENDLIST damit
        /// Emby die Datei als wachsenden Stream behandelt.
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

        // ────────────────────────────────────────────────────────────
        //  Segment-Equalization: Die schnellere Qualität wird sofort
        //  gestoppt. Die langsamere muxt alleine weiter bis sie den
        //  gleichen Segment-Stand erreicht hat, dann wird sie auch gestoppt.
        // ────────────────────────────────────────────────────────────
        private static async Task EqualizeBeforeStop(string videoId, string triggeringKey)
        {
            // Alle laufenden Prozesse für dieses Video finden
            var siblings = new List<(string key, int segs)>();
            foreach (var kvp in RunningProcesses)
            {
                if (!kvp.Key.StartsWith(videoId + "_", StringComparison.Ordinal))
                    continue;

                var dir = Path.Combine(CacheDir, kvp.Key);
                var m3u8 = Path.Combine(dir, "stream.m3u8");
                int segs = 0;
                try
                {
                    if (File.Exists(m3u8))
                        segs = CountSegments(File.ReadAllText(m3u8));
                }
                catch { }
                siblings.Add((kvp.Key, segs));
            }

            if (siblings.Count < 2)
            {
                Log($"Equalize: Only 1 quality running for {videoId}, skipping");
                return;
            }

            int maxSegs = siblings.Max(s => s.segs);
            // Triggernde Qualität NICHT sofort stoppen — ihr Watchdog läuft noch
            // und muss die Equalization zu Ende führen
            var ahead = siblings
                .Where(s => s.segs >= maxSegs && s.key != triggeringKey)
                .ToList();
            var behind = siblings
                .Where(s => s.segs < maxSegs && s.key != triggeringKey)
                .ToList();

            // ── Schnellere Qualität(en) sofort stoppen ──
            foreach (var (key, segs) in ahead)
            {
                Log($"Equalize: Stopping faster quality {key} ({segs} segs)");
                StopAndMarkIdle(key);
            }

            if (behind.Count == 0)
            {
                Log($"Equalize: All {siblings.Count} qualities at {maxSegs} segs, nothing more to do");
                return;
            }

            // ── Langsamere Qualität(en) weiter muxen lassen bis sie aufgeholt haben (unbegrenzt) ──
            foreach (var (key, segs) in behind)
                Log($"Equalize: {key} has {segs}/{maxSegs} segs — waiting until caught up");

            const int pollMs = 1000;
            var pendingKeys = new HashSet<string>(behind.Select(b => b.key));

            while (pendingKeys.Count > 0)
            {
                await Task.Delay(pollMs).ConfigureAwait(false);

                foreach (var key in pendingKeys.ToList())
                {
                    if (!RunningProcesses.TryGetValue(key, out var proc))
                    {
                        // Prozess schon beendet (fertig oder Fehler)
                        pendingKeys.Remove(key);
                        continue;
                    }
                    try { if (proc.HasExited) { pendingKeys.Remove(key); continue; } }
                    catch { pendingKeys.Remove(key); continue; }

                    var dir = Path.Combine(CacheDir, key);
                    var m3u8 = Path.Combine(dir, "stream.m3u8");
                    int currentSegs = 0;
                    try
                    {
                        if (File.Exists(m3u8))
                            currentSegs = CountSegments(File.ReadAllText(m3u8));
                    }
                    catch { }

                    if (currentSegs >= maxSegs)
                    {
                        // Ziel erreicht → sofort stoppen damit nicht weiter gemuxt wird
                        Log($"Equalize: {key} reached {currentSegs}/{maxSegs} segs — stopping now");
                        StopAndMarkIdle(key);
                        pendingKeys.Remove(key);
                    }
                }
            }

            Log($"Equalize: All qualities for {videoId} equalized");
        }

        /// <summary>
        /// Stoppt einen einzelnen FFmpeg-Prozess und schreibt den idle_stopped Marker.
        /// </summary>
        private static void StopAndMarkIdle(string processKey)
        {
            if (!RunningProcesses.TryRemove(processKey, out var proc))
                return;

            try { if (!proc.HasExited) proc.Kill(); }
            catch { }

            try
            {
                var dir = Path.Combine(CacheDir, processKey);
                var m3u8 = Path.Combine(dir, "stream.m3u8");
                var playbackPath = Path.Combine(dir, "playback.m3u8");
                var idleMarker = Path.Combine(dir, "idle_stopped");

                int segs = 0;
                if (File.Exists(m3u8))
                    segs = CountSegments(File.ReadAllText(m3u8));

                File.WriteAllText(idleMarker, $"{DateTime.UtcNow:O}\n{segs}");

                UpdatePlaybackM3u8(m3u8, playbackPath);
                if (File.Exists(playbackPath))
                {
                    var pc = File.ReadAllText(playbackPath);
                    if (!pc.Contains("#EXT-X-ENDLIST"))
                        File.WriteAllText(playbackPath, pc + "\n#EXT-X-ENDLIST\n");
                }

                Log($"StopAndMarkIdle: {processKey} stopped ({segs} segs)");
            }
            catch (Exception ex)
            {
                Log($"StopAndMarkIdle error for {processKey}: {ex.Message}");
            }

            MuxGate.Release();
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

        public static async Task<string?> MuxToHlsAsync(
            string directVideoUrl, string directAudioUrl,
            string videoId, int height, bool isVp9 = false,
            string? authHeader = null,
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
                    Log($"FFmpeg already running for {processKey}, waiting...");
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
                    if (File.Exists(playback))
                    {
                        var fc = File.ReadAllText(playback);
                        if (CountSegments(fc) >= 1) return playback;
                    }
                    Log("Wait timeout for running process");
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

                if (File.Exists(m3u8))
                {
                    var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(m3u8);
                    if (age.TotalDays < GetCacheDays())
                    {
                        var cached = File.ReadAllText(m3u8);

                        // Vollständiger Cache → sofort zurückgeben
                        if (cached.Contains("#EXT-X-ENDLIST")
                            && CountSegments(cached) >= MinSegmentsForCache)
                        {
                            Log($"Cache hit ({CountSegments(cached)} segments)");
                            UpdatePlaybackM3u8(m3u8, playback);
                            return File.Exists(playback) ? playback : m3u8;
                        }

                        // Partieller Cache (idle-gestoppt) → Resume vorbereiten
                        var idleMarker = Path.Combine(videoDir, "idle_stopped");
                        if (File.Exists(idleMarker) && CountSegments(cached) >= MinSegmentsForCache)
                        {
                            Log($"RESUME: Found partial cache ({CountSegments(cached)} segs) for {processKey}");
                            // Nicht löschen — weiter unten wird mit -ss fortgesetzt
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

                if (!await GetMuxGate().WaitAsync(TimeSpan.FromSeconds(30), ct)
                    .ConfigureAwait(false))
                {
                    Log($"FAIL: Too many muxes ({ActiveMuxCount}), rejecting {processKey}");
                    return null;
                }

                bool semaphoreOwnedByCaller = true;
                try
                {
                    var ffmpeg = FindFfmpeg();

                    string segExt = isVp9 ? "m4s" : "ts";

                    // ── Resume-Erkennung ──
                    int startNumber = 0;
                    string seekArg = "";
                    var idleMarkerPath = Path.Combine(videoDir, "idle_stopped");

                    if (File.Exists(idleMarkerPath) && File.Exists(m3u8))
                    {
                        var existingContent = File.ReadAllText(m3u8);
                        int existingSegs = CountSegments(existingContent);
                        double existingDuration = ParseTotalDuration(existingContent);

                        if (existingSegs >= MinSegmentsForCache && existingDuration > 0)
                        {
                            startNumber = existingSegs;
                            // 2s Overlap für sauberen Übergang
                            double seekSec = Math.Max(0, existingDuration - 2);
                            seekArg = $"-ss {seekSec:F2} ";
                            Log($"RESUME: Starting at seg {startNumber}, seeking to {seekSec:F1}s (total cached: {existingDuration:F1}s)");

                            // Idle-Marker löschen
                            try { File.Delete(idleMarkerPath); } catch { }

                            // #EXT-X-ENDLIST aus stream.m3u8 entfernen falls vorhanden
                            // und #EXT-X-DISCONTINUITY einfügen → signalisiert Emby
                            // dass sich Timestamps/Codecs ändern können (kein Stottern)
                            if (existingContent.Contains("#EXT-X-ENDLIST"))
                            {
                                existingContent = existingContent.Replace("#EXT-X-ENDLIST", "").TrimEnd();
                            }
                            existingContent += "\n#EXT-X-DISCONTINUITY\n";
                            File.WriteAllText(m3u8, existingContent);
                        }
                        else
                        {
                            try { File.Delete(idleMarkerPath); } catch { }
                        }
                    }

                    var segPattern = Path.Combine(videoDir, $"seg_%04d.{segExt}");

                    string codecArgs = isVp9
                        ? "-c:v copy -c:a copy -hls_segment_type fmp4"
                        : "-c:v copy -c:a copy -bsf:v h264_mp4toannexb";

                    // ── Speed-Optimierungen ──
                    // -headers: Basic Auth für Invidious-Proxy
                    // -thread_queue_size 4096: größerer Input-Buffer pro Stream
                    // -fflags +nobuffer: kein interner Buffering-Delay
                    // -flags low_delay: minimale Latenz
                    // -hls_init_time 1: erstes Segment in ~1s fertig
                    // -hls_time 4: weitere Segmente à 4s
                    string headersArg = !string.IsNullOrEmpty(authHeader)
                        ? $"-headers \"Authorization: {authHeader}\r\n\" "
                        : "";

                    var args =
                        $"-y " +
                        $"-probesize 10M -analyzeduration 5M " +
                        $"{seekArg}" +
                        $"-reconnect 1 -reconnect_streamed 1 " +
                        $"-reconnect_delay_max 5 " +
                        $"-thread_queue_size 4096 " +
                        $"{headersArg}" +
                        $"-i \"{directVideoUrl}\" " +
                        $"{seekArg}" +
                        $"-reconnect 1 -reconnect_streamed 1 " +
                        $"-reconnect_delay_max 5 " +
                        $"-thread_queue_size 4096 " +
                        $"{headersArg}" +
                        $"-i \"{directAudioUrl}\" " +
                        $"-map 0:v:0 -map 1:a:0 " +
                        $"-fflags +genpts+discardcorrupt " +
                        $"-avoid_negative_ts make_zero " +
                        $"-max_muxing_queue_size 4096 " +
                        $"{codecArgs} " +
                        $"-f hls -hls_time 4 " +
                        $"-hls_list_size 0 -hls_flags append_list " +
                        $"-start_number {startNumber} " +
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

                            var wasRemoved = RunningProcesses.TryRemove(processKey, out _);
                            // Nur releasen wenn WIR den Prozess entfernt haben
                            // (nicht wenn der Idle-Watchdog es schon getan hat)
                            if (wasRemoved) MuxGate.Release();
                            try { process.Dispose(); } catch { }
                        }
                    });

                    _ = Task.Run(async () =>
                    {
                        int lastSegCount = 0;
                        DateTime lastNewSegTime = DateTime.UtcNow;

                        while (RunningProcesses.ContainsKey(processKey))
                        {
                            UpdatePlaybackM3u8(m3u8, playback);

                            try
                            {
                                int currentSegs = 0;
                                if (File.Exists(m3u8))
                                    currentSegs = CountSegments(File.ReadAllText(m3u8));

                                if (currentSegs > lastSegCount)
                                {
                                    lastSegCount = currentSegs;
                                    lastNewSegTime = DateTime.UtcNow;
                                }

                                // ── Stall-Detection (60s keine neuen Segmente) ──
                                if ((DateTime.UtcNow - lastNewSegTime).TotalMilliseconds >= IdleTimeoutMs)
                                {
                                    // FFmpeg schreibt keine neuen Segmente mehr
                                    bool processStillRunning = false;
                                    if (RunningProcesses.TryGetValue(processKey, out var checkProc))
                                    {
                                        try { processStillRunning = !checkProc.HasExited; }
                                        catch { }
                                    }

                                    if (!processStillRunning)
                                        break;

                                    Log($"IDLE: No new segments for {IdleTimeoutMs / 1000}s ({currentSegs} segs), equalizing {processKey}");

                                    await EqualizeBeforeStop(videoId, processKey).ConfigureAwait(false);

                                    Log($"IDLE: Equalization done for {videoId}");
                                    StopAndMarkIdle(processKey);
                                    break;
                                }
                            }
                            catch (Exception ex)
                            {
                                Log($"Idle-check error for {processKey}: {ex.Message}");
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
                int cacheDays = GetCacheDays();
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