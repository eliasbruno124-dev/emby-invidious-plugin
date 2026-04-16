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

        // Koordination: Nur EIN Watchdog pro Video darf Stop+Equalization durchführen
        private static readonly ConcurrentDictionary<string, bool> StopInProgress = new();

        // Verhindert doppelten Resume-Start durch den Session-Monitor
        private static readonly ConcurrentDictionary<string, bool> ResumeInProgress = new();

        private static readonly object LogLock = new();
        private static CancellationTokenSource? _monitorCts;

        private const int SessionCheckMs = 2000;   // alle 2s Session prüfen

        /// <summary>
        /// Prüft ob irgendeine Emby-Session gerade dieses Video abspielt.
        /// </summary>
        private static bool IsVideoBeingPlayed(string videoId)
        {
            try
            {
                var sm = Plugin.SessionManager;
                if (sm == null) return false;

                foreach (var session in sm.Sessions)
                {
                    var item = session.NowPlayingItem;
                    if (item != null)
                    {
                        var path = item.Path ?? "";
                        if (path.Contains(videoId, StringComparison.OrdinalIgnoreCase))
                            return true;
                    }

                    var sourceId = session.PlayState?.MediaSourceId ?? "";
                    if (sourceId.Contains(videoId, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
            catch (Exception ex)
            {
                Log($"IsVideoBeingPlayed error: {ex.Message}");
            }
            return false;
        }

        private const int MinSegmentsForPlayback = 3;
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
            // VP9/4K braucht viel länger zum Seekieren und Demuxen
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
        /// Startup-Logik. Wird im static ctor aufgerufen und kann bei Plugin-Reload
        /// über EnsureInitialized() erneut aufgerufen werden.
        /// </summary>
        private static void Initialize()
        {
            Log("=== MuxHelper initialized ===");
            Log($"CacheDir: {CacheDir}");
            Log($"FFmpeg: {FindFfmpeg()}");

            // Statische Dictionaries leeren (nötig bei Plugin-Reload im selben AppDomain)
            RunningProcesses.Clear();
            StopInProgress.Clear();
            ResumeInProgress.Clear();

            // Verwaiste FFmpeg-Prozesse killen die noch vom letzten Emby-Lauf stammen
            KillOrphanedFfmpegProcesses();

            MarkOrphanedCachesForResume();
            CleanOldDirs();

            // Alten Monitor stoppen falls noch aktiv (Plugin-Reload)
            try { _monitorCts?.Cancel(); } catch { }
            try { _monitorCts?.Dispose(); } catch { }
            _monitorCts = new CancellationTokenSource();
            StartSessionResumeMonitor(_monitorCts.Token);
        }

        /// <summary>
        /// Kann von außen aufgerufen werden um den MuxHelper neu zu initialisieren
        /// (z.B. nach Plugin-Reload). Ist thread-safe und idempotent.
        /// </summary>
        private static bool _initialized;
        private static readonly object InitLock = new();
        public static void EnsureInitialized()
        {
            if (_initialized) return;
            lock (InitLock)
            {
                if (_initialized) return;
                _initialized = true;
                // Static ctor hat schon Initialize() aufgerufen.
                // Falls Plugin-Reload: Re-Initialize.
            }
        }

        /// <summary>
        /// Killt alle FFmpeg-Prozesse die unser Cache-Verzeichnis verwenden.
        /// Nötig weil auf Windows Child-Prozesse den Parent-Kill überleben.
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
                        // Kill alle ffmpeg-Prozesse die VOR dem aktuellen
                        // Emby-Start gestartet wurden (= Überbleibsel vom letzten Lauf).
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
        /// Beim Startup: Cache-Dirs ohne ENDLIST und ohne idle_stopped Marker
        /// stammen von FFmpeg-Prozessen die beim letzten Shutdown noch liefen.
        /// → idle_stopped Marker schreiben damit Resume beim nächsten Playback greift.
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
                    if (File.Exists(idleMarker)) continue; // schon markiert

                    try
                    {
                        var content = SafeReadAllText(m3u8);
                        if (content == null) continue;
                        if (content.Contains("#EXT-X-ENDLIST")) continue; // komplett gemuxt

                        int segs = CountSegments(content);
                        if (segs < MinSegmentsForResume) continue; // zu wenig zum Resumen

                        // Nur markieren wenn auch Resume-Metadaten vorhanden sind
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

            // Monitor stoppen
            try { _monitorCts?.Cancel(); } catch { }

            // Alle FFmpeg-Prozesse killen
            KillAll();

            // CTS aufräumen
            try { _monitorCts?.Dispose(); _monitorCts = null; } catch { }

            // Statische Dictionaries leeren damit bei Plugin-Reload
            // keine Geister-Einträge bleiben
            StopInProgress.Clear();
            ResumeInProgress.Clear();

            // Für alle Cache-Dirs die kein ENDLIST haben: idle_stopped schreiben
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
        //  Dauer-basierte Equalization: Die schnellere Qualität wird sofort
        //  gestoppt. Die langsamere muxt alleine weiter bis sie die gleiche
        //  DAUER (nicht Segment-Anzahl!) erreicht hat, dann wird sie auch gestoppt.
        //  → VP9 und H264 haben unterschiedliche Segment-Längen (3-4s vs 5.5s)
        //    → Segment-Count-Vergleich führt zu ungleicher Content-Dauer.
        // ────────────────────────────────────────────────────────────
        private static async Task EqualizeBeforeStop(string videoId, string triggeringKey)
        {
            // Alle laufenden Prozesse für dieses Video finden
            // SafeReadAllText verhindert Race-Conditions mit FFmpeg's m3u8.tmp-Rename
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
            // Triggernde Qualität NICHT sofort stoppen — ihr Watchdog läuft noch
            // und muss die Equalization zu Ende führen
            var ahead = siblings
                .Where(s => s.dur >= maxDur && s.key != triggeringKey)
                .ToList();
            // Triggernde Qualität MIT einbeziehen falls sie hinterher ist
            var behind = siblings
                .Where(s => s.dur < maxDur)
                .ToList();

            // ── Schnellere Qualität(en) sofort stoppen ──
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

            // ── Langsamere Qualität(en) weiter muxen lassen bis sie aufgeholt haben ──
            foreach (var (key, dur, segs) in behind)
                Log($"Equalize: {key} has {dur:F1}s/{maxDur:F1}s ({segs} segs) — waiting until caught up");

            const int pollMs = 2000;
            const int maxWaitMs = 300_000; // 5 Minuten max
            var pendingKeys = new HashSet<string>(behind.Select(b => b.key));
            int elapsed = 0;

            while (pendingKeys.Count > 0 && elapsed < maxWaitMs)
            {
                await Task.Delay(pollMs).ConfigureAwait(false);
                elapsed += pollMs;

                foreach (var key in pendingKeys.ToList())
                {
                    if (!RunningProcesses.TryGetValue(key, out var proc))
                    {
                        Log($"Equalize: {key} process ended during catch-up");
                        pendingKeys.Remove(key);
                        continue;
                    }
                    try { if (proc.HasExited) { pendingKeys.Remove(key); continue; } }
                    catch { pendingKeys.Remove(key); continue; }

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
                // KEIN ENDLIST hinzufügen — der Player soll weiter pollen,
                // damit der Session-Monitor bei erneutem Abspielen
                // FFmpeg fortsetzen kann und neue Segmente erscheinen.

                Log($"StopAndMarkIdle: {processKey} stopped ({segs} segs)");
            }
            catch (Exception ex)
            {
                Log($"StopAndMarkIdle error for {processKey}: {ex.Message}");
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
        //  Session-Resume-Monitor: Prüft periodisch ob eine Session
        //  ein Video abspielt, das einen idle_stopped Cache hat.
        //  Falls ja → FFmpeg-Resume starten.
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

                    // Läuft bereits ein Prozess für dieses Video?
                    if (RunningProcesses.ContainsKey(dirName)) continue;
                    // Wird bereits resumed?
                    if (ResumeInProgress.ContainsKey(dirName)) continue;

                    // Wird dieses Video gerade abgespielt?
                    if (!IsVideoBeingPlayed(videoId)) continue;

                    // Resume-Metadaten lesen
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

                        // URLs aus aktueller Config rekonstruieren (robust gegen URL-Änderungen)
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

                        // ENDLIST aus playback.m3u8 entfernen
                        PrepareForResume(videoId, height);

                        // FFmpeg-Resume im Hintergrund starten
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
                    // Nach Wartezeit: Prozess ist fertig, überprüfe ob Daten da sind
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
                        if (File.Exists(idleMarker) && CountSegments(cached) >= MinSegmentsForResume)
                        {
                            Log($"RESUME: Found partial cache ({CountSegments(cached)} segs) for {processKey}");
                            resumeAttempted = true;
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

                try
                {
                    var ffmpeg = FindFfmpeg();

                    string segExt = isVp9 ? "m4s" : "ts";

                    // ── Resume-Erkennung ──
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
                                // Exakt an der letzten Position fortsetzen (kein Overlap)
                                // FFmpeg input-seek springt zum nächsten Keyframe davor,
                                // append_list setzt die Timeline nahtlos fort.
                                double seekSec = existingDuration;
                                seekArg = $"-ss {seekSec.ToString("F2", CultureInfo.InvariantCulture)} ";
                                Log($"RESUME: {existingSegs} segs cached ({existingDuration.ToString("F1", CultureInfo.InvariantCulture)}s), seeking to {seekSec.ToString("F1", CultureInfo.InvariantCulture)}s");

                                // Idle-Marker löschen
                                try { File.Delete(idleMarkerPath); } catch { }

                                // #EXT-X-ENDLIST entfernen damit Emby weiter pollt.
                                // KEIN DISCONTINUITY einfügen — das bricht Emby's Seeking.
                                // append_list übernimmt Segment-Nummerierung automatisch.
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
                        // Marker fehlt aber resumeAttempted ist true - cleanup nötig
                        Log($"Resume marker missing, re-muxing from scratch");
                        try { Directory.Delete(videoDir, true); } catch { }
                        Directory.CreateDirectory(videoDir);
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

                    // Bei Resume: kein -avoid_negative_ts make_zero, damit PTS
                    // aus dem Input-Seek beibehalten werden und die HLS-Timeline
                    // nahtlos fortgesetzt wird. Auch kein -start_number, da
                    // append_list die Segment-Nummerierung aus der bestehenden
                    // m3u8 automatisch fortsetzt.
                    string avoidNegTs = isResume ? "" : "-avoid_negative_ts make_zero ";

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
                        $"{avoidNegTs}" +
                        $"-max_muxing_queue_size 4096 " +
                        $"{codecArgs} " +
                        $"-f hls -hls_time 4 " +
                        $"-hls_list_size 0 -hls_flags append_list " +
                        $"-hls_segment_filename \"{segPattern}\" \"{m3u8}\"";

                    // Resume-Metadaten speichern (itags für URL-Rekonstruktion)
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

                            RunningProcesses.TryRemove(processKey, out _);
                            try { process.Dispose(); } catch { }
                        }
                    });

                    _ = Task.Run(async () =>
                    {
                        int lastSegCount = 0;
                        DateTime lastNewSegTime = DateTime.UtcNow;
                        DateTime lastSessionSeen = DateTime.UtcNow;
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

                                // ── Session-Check alle ~2s ──
                                tickCounter++;
                                if ((tickCounter % (SessionCheckMs / PlaybackUpdateIntervalMs)) == 0)
                                {
                                    if (IsVideoBeingPlayed(videoId))
                                        lastSessionSeen = DateTime.UtcNow;
                                }

                                bool activeSession = (DateTime.UtcNow - lastSessionSeen).TotalMilliseconds < GetSessionGraceMs();

                                // ── Pre-Buffer-Limit: Kein Zuschauer → nach X Segmenten stoppen ──
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

                                    // Stop/Equalize nur von der höchsten Qualitätsstufe starten,
                                    // damit höhere Qualitäten nicht vorzeitig separat gestoppt werden.
                                    if (!IsHighestQualityForVideo(videoId, processKey))
                                    {
                                        await Task.Delay(1000).ConfigureAwait(false);
                                        continue;
                                    }

                                    // Nur EIN Watchdog pro Video darf stoppen
                                    if (!StopInProgress.TryAdd(videoId, true))
                                    {
                                        // Anderer Watchdog stoppt bereits → warten bis er fertig ist
                                        Log($"STOP: Another watchdog already stopping {videoId}, waiting...");
                                        while (StopInProgress.ContainsKey(videoId))
                                            await Task.Delay(500).ConfigureAwait(false);
                                        break;
                                    }

                                    try
                                    {
                                        string reason = noSession ? "no session" : "FFmpeg stalled";
                                        Log($"STOP: {reason} ({currentSegs} segs), equalizing {processKey}");

                                        await EqualizeBeforeStop(videoId, processKey).ConfigureAwait(false);
                                        Log($"STOP: Equalization done for {videoId}");

                                        // ALLE Qualitäten dieses Videos stoppen (inkl. triggernde)
                                        var allKeys = RunningProcesses.Keys
                                            .Where(k => k.StartsWith(videoId + "_", StringComparison.Ordinal))
                                            .ToList();
                                        foreach (var key in allKeys)
                                            StopAndMarkIdle(key);

                                        // Triggernde Qualität selbst stoppen falls noch da
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