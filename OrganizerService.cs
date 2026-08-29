using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Playnite.SDK;

namespace GameSnapPlugin
{
    public class OrganizerService
    {
        private readonly GameSnapSettings   _settings;
        private readonly DictionaryService  _dictionary;
        private readonly GameSnapLogger     _logger;
        private readonly IPlayniteAPI?      _api;

        // Callback para notificações (injetado pelo plugin principal)
        public Action<string, string>? OnFileMoved { get; set; }

        // Lista de jogos organizados neste ciclo — para notificar ScreenshotsVisualizer
        public Action<List<string>>? OnGamesOrganized { get; set; }

        // Jogo atual informado pelo Playnite
        private string? _currentGame;

        // Cache de arquivos já processados nesta sessão
        private readonly HashSet<string> _processed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // ✅ 新增：通知限制跟踪
        private readonly HashSet<string> _notifiedMismatches = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _notifiedErrors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // ✅ 新增：文件夹不存在错误通知跟踪
        private readonly HashSet<string> _notifiedFolderErrors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // ✅ 新增：移动失败错误通知跟踪（按文件路径去重）
        private readonly HashSet<string> _notifiedMoveErrors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public OrganizerService(GameSnapSettings settings, DictionaryService dictionary, GameSnapLogger logger, IPlayniteAPI? api = null)
        {
            _settings   = settings;
            _dictionary = dictionary;
            _logger     = logger;
            _api        = api;
        }

        // ─── 处理单个文件（仅用于 Watcher 新文件） ────────────────────

        public void ProcessSingleFile(string filePath)
        {
            var dict = _dictionary.Load();
            var allSources = new List<string>();

            if (!string.IsNullOrEmpty(_settings.SourceFolder))
                allSources.Add(_settings.SourceFolder);

            allSources.AddRange(_settings.AdditionalSourceFolders
                .Where(f => !string.IsNullOrEmpty(f) && Directory.Exists(f)));

            if (allSources.Count == 0) return;
            if (!Directory.Exists(_settings.DestinationBase)) return;

            var folders = LoadFolders();
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var matchScores = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            // 只处理单个文件
            TryOrganizeFile(filePath, dict, folders, counts, matchScores);

            // 如果有文件被处理，发送汇总通知
            if (counts.Count > 0)
            {
                var total = counts.Values.Sum();
                var summary = string.Join(" | ", counts.Select(kv =>
                {
                    var gameName = _currentGame ?? kv.Key;
                    return $"{gameName}→\"{kv.Key}\" ({kv.Value})";
                }));
                var maxScore = matchScores.Values.Max();
                var scoreText = maxScore > 0 ? $"{maxScore}%" : "0%";

                _logger.Info($"Organized: {summary}");

                // ✅ 只有在 NotifyOnEachScreenshot 启用时才发送通知
                // 否则只记录日志，不发送通知
                if (_settings.NotifyOnEachScreenshot)
                {
                    var organizedText = _api?.Resources.GetString("LOCGameSnap_Summary_Organized") ?? "Organized";
                    var screenshotText = _api?.Resources.GetString("LOCGameSnap_Summary_Screenshot") ?? "screenshot(s)";
                    var scorePrefix = _api?.Resources.GetString("LOCGameSnap_Notification_Score") ?? "[Score: {0}]";

                    var message = $"{organizedText} {total} {screenshotText}: {summary}  {string.Format(scorePrefix, scoreText)}";
                    OnFileMoved?.Invoke("GameSnap", message);
                }

                OnGamesOrganized?.Invoke(counts.Keys.ToList());
            }
        }

        public void SetCurrentGame(string? name)
        {
            _currentGame = name;

            // ✅ 新增：游戏切换时重置通知限制（如果限制通知开启）
            if (_settings.LimitNotifications)
            {
                _notifiedMismatches.Clear();
                _notifiedErrors.Clear();
                _notifiedFolderErrors.Clear();
                _notifiedMoveErrors.Clear(); // ✅ 新增
            }
        }
        // ✅ 新增：获取当前游戏名
        public string? GetCurrentGame()
        {
            return _currentGame;
        }

        // ──────────────────────────────────────────────
        // Entry point — chamado pelo watcher e pelo loop
        // ──────────────────────────────────────────────
        public SteamService? SteamService { get; set; }
        public EmulatorService? EmulatorService { get; set; }

        public void Organize()
        {
            var dict = _dictionary.Load();

            // Steam screenshots
            if (_settings.EnableSteamSupport && SteamService != null)
                OrganizeSteam();

            // Emulator screenshots
            if (_settings.EnableEmulatorSupport && EmulatorService != null)
                OrganizeEmulators(dict);

            var allSources = new List<string>();

            if (!string.IsNullOrEmpty(_settings.SourceFolder))
                allSources.Add(_settings.SourceFolder);

            allSources.AddRange(_settings.AdditionalSourceFolders
                .Where(f => !string.IsNullOrEmpty(f) && Directory.Exists(f)));

            if (allSources.Count == 0) return;
            if (!Directory.Exists(_settings.DestinationBase)) return;

            var folders = LoadFolders();
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var matchScores = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (var source in allSources)
            {
                if (!Directory.Exists(source)) continue;
                foreach (var file in Directory.GetFiles(source))
                {
                    TryOrganizeFile(file, dict, folders, counts, matchScores);
                }
            }

            if (counts.Count > 0)
            {
                var total = counts.Values.Sum();
                var summary = string.Join(" | ", counts.Select(kv =>
                {
                    var gameName = _currentGame ?? kv.Key;
                    return $"{gameName}→\"{kv.Key}\" ({kv.Value})";
                }));
                var maxScore = matchScores.Values.Max();
                var scoreText = maxScore > 0 ? $"{maxScore}%" : "0%";

                _logger.Info($"Organized: {summary}");

                // ✅ 只有在 NotifyOnEachScreenshot 启用时才发送通知
                // 否则只记录日志，不发送通知
                if (_settings.NotifyOnEachScreenshot)
                {
                    var organizedText = _api?.Resources.GetString("LOCGameSnap_Summary_Organized") ?? "Organized";
                    var screenshotText = _api?.Resources.GetString("LOCGameSnap_Summary_Screenshot") ?? "screenshot(s)";
                    var scorePrefix = _api?.Resources.GetString("LOCGameSnap_Notification_Score") ?? "[Score: {0}]";

                    var message = $"{organizedText} {total} {screenshotText}: {summary}  {string.Format(scorePrefix, scoreText)}";
                    OnFileMoved?.Invoke("GameSnap", message);
                }

                OnGamesOrganized?.Invoke(counts.Keys.ToList());
            }
        }

        // ──────────────────────────────────────────────
        // Steam
        // ──────────────────────────────────────────────
        private void OrganizeSteam()
        {
            if (SteamService == null) return;

            var steamPath = !string.IsNullOrEmpty(_settings.SteamPath)
                ? _settings.SteamPath
                : SteamService.DetectSteamPath() ?? "";

            if (string.IsNullOrEmpty(steamPath)) return;

            var pending = SteamService.GetPendingScreenshots(steamPath);
            if (pending.Count == 0) return;

            var folders = LoadFolders();
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var matchScores = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (var ss in pending)
            {
                if (_processed.Contains(ss.FilePath)) continue;

                var gameName = SteamService.ResolveGameName(ss.AppId);
                if (gameName == null)
                {
                    _logger.Write(LogType.Error,
                        $"Steam: AppID {ss.AppId} not found in library. File: {Path.GetFileName(ss.FilePath)}");
                    TryMoveToUnmatched(ss.FilePath, Path.GetExtension(ss.FilePath).ToLowerInvariant());
                    continue;
                }

                var matchResult = FindBestMatchByScore(gameName, folders, 0);
                var match = matchResult.Folder;

                if (match == null)
                {
                    _logger.Write(LogType.Error,
                        $"Steam: No folder for '{gameName}'. File: {Path.GetFileName(ss.FilePath)}");
                    TryMoveToUnmatched(ss.FilePath, Path.GetExtension(ss.FilePath).ToLowerInvariant());
                    continue;
                }

                // Record match score
                if (!matchScores.ContainsKey(match.NameOriginal) || matchResult.Score > matchScores[match.NameOriginal])
                {
                    matchScores[match.NameOriginal] = matchResult.Score;
                }

                var ext = Path.GetExtension(ss.FilePath).ToLowerInvariant();
                var date = GetBestDate(ss.FilePath);
                var destName = BuildDestName(match.NameOriginal, date,
                                            Path.GetFileNameWithoutExtension(ss.FilePath), ext);
                var destPath = Path.Combine(match.Path, destName);

                int i = 1;
                while (File.Exists(destPath))
                {
                    var nameNoExt = Path.GetFileNameWithoutExtension(destName);
                    destPath = Path.Combine(match.Path, $"{nameNoExt}_{i}{ext}");
                    i++;
                }

                try
                {
                    File.Move(ss.FilePath, destPath);

                    if (_settings.EnableBackup && !string.IsNullOrEmpty(_settings.BackupFolder))
                        TryBackup(destPath, match.NameOriginal, false);

                    _processed.Add(ss.FilePath);

                    int current = counts.ContainsKey(match.NameOriginal) ? counts[match.NameOriginal] : 0;
                    counts[match.NameOriginal] = current + 1;

                    _logger.Write(LogType.Move,
                        $"Steam: {Path.GetFileName(ss.FilePath)} → {match.NameOriginal} (Score: {matchResult.Score}%)");
                }
                catch (Exception ex)
                {
                    _logger.Write(LogType.Error, $"Steam move failed: {ex.Message}");

                    // ✅ 新增：发送错误通知
                    if (_settings.EnableMismatchNotification)
                    {
                        var fileName = Path.GetFileName(ss.FilePath);
                        var errorKey = $"STEAM_ERROR_{ss.AppId}_{fileName}";
                        bool shouldNotify = !_settings.LimitNotifications || !_notifiedErrors.Contains(errorKey);

                        if (shouldNotify)
                        {
                            var unknownText = _api?.Resources.GetString("LOCGameSnap_GameName_Unknown") ?? "Unknown";
                            var errorTemplate = _api?.Resources.GetString("LOCGameSnap_Error_SteamMoveFailed") ??
                                "❌ Error: Steam screenshot ({1}) for game ({0}) failed to organize";
                            var message = string.Format(errorTemplate, gameName ?? unknownText, fileName);
                            OnFileMoved?.Invoke("GameSnap", message);

                            if (_settings.LimitNotifications)
                            {
                                _notifiedErrors.Add(errorKey);
                            }
                        }
                    }
                }
            }

            if (counts.Count > 0)
            {
                var total = counts.Values.Sum();
                var summary = string.Join(" | ", counts.Select(kv =>
                {
                    var gameName = _currentGame ?? kv.Key;
                    return $"{gameName}→\"{kv.Key}\" ({kv.Value})";
                }));
                var maxScore = matchScores.Values.Max();
                var scoreText = maxScore > 0 ? $"{maxScore}%" : "0%";

                _logger.Info($"Organized: {summary}");

                // ✅ 只有在 NotifyOnEachScreenshot 启用时才发送通知
                // 否则只记录日志，不发送通知
                if (_settings.NotifyOnEachScreenshot)
                {
                    var steamLabel = _api?.Resources.GetString("LOCGameSnap_Summary_Steam") ?? "Steam";
                    var screenshotText = _api?.Resources.GetString("LOCGameSnap_Summary_Screenshot") ?? "screenshot(s)";
                    var scorePrefix = _api?.Resources.GetString("LOCGameSnap_Notification_Score") ?? "[Score: {0}]";

                    var message = $"{steamLabel}: {total} {screenshotText}: {summary}  {string.Format(scorePrefix, scoreText)}";
                    OnFileMoved?.Invoke("GameSnap", message);
                }

                OnGamesOrganized?.Invoke(counts.Keys.ToList());
            }
        }

        // ──────────────────────────────────────────────
        // Emulators
        // ──────────────────────────────────────────────
        private void OrganizeEmulators(Dictionary<string, string> dict)
        {
            if (EmulatorService == null) return;

            var pending = EmulatorService.GetPendingScreenshots();
            if (pending.Count == 0) return;

            var folders = LoadFolders();
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var matchScores = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (var ss in pending)
            {
                if (_processed.Contains(ss.FilePath)) continue;

                var resolvedName = ss.GameName;
                var normCandidate = DictionaryService.Normalize(ss.GameName);
                if (dict.TryGetValue(normCandidate, out var fromDict))
                    resolvedName = fromDict;

                var matchResult = FindBestMatchByScore(resolvedName, folders, 0);
                var match = matchResult.Folder;

                if (match == null)
                {
                    // 使用 ForceCreateFolder 替代 AutoCreateFolders
                    if (_settings.ForceCreateFolder)
                    {
                        _logger.Write(LogType.Info, $"Emulator: No folder found, ForceCreateFolder enabled, creating folder...");
                        var folderPath = CreateFolderForGame(resolvedName);
                        if (!string.IsNullOrEmpty(folderPath))
                        {
                            folders = LoadFolders(); // refresh
                            match = folders.FirstOrDefault(f =>
                                DictionaryService.Normalize(f.NameOriginal) == DictionaryService.Normalize(Path.GetFileName(folderPath)));

                            if (match != null)
                            {
                                _logger.Write(LogType.Info, $"Emulator: Created folder: '{match.NameOriginal}'");
                            }
                        }
                    }

                    if (match == null)
                    {
                        _logger.Write(LogType.Error,
                            $"Emulator [{ss.Emulator}]: No folder for '{resolvedName}'. File: {Path.GetFileName(ss.FilePath)}");
                        TryMoveToUnmatched(ss.FilePath, Path.GetExtension(ss.FilePath).ToLowerInvariant());
                        continue;
                    }
                }

                // Record match score
                if (!matchScores.ContainsKey(match.NameOriginal) || matchResult.Score > matchScores[match.NameOriginal])
                {
                    matchScores[match.NameOriginal] = matchResult.Score;
                }

                var ext = Path.GetExtension(ss.FilePath).ToLowerInvariant();
                var date = GetBestDate(ss.FilePath);
                var destName = BuildDestName(match.NameOriginal, date,
                                            Path.GetFileNameWithoutExtension(ss.FilePath), ext);
                var destPath = Path.Combine(match.Path, destName);

                int i = 1;
                while (File.Exists(destPath))
                {
                    var nameNoExt = Path.GetFileNameWithoutExtension(destName);
                    destPath = Path.Combine(match.Path, $"{nameNoExt}_{i}{ext}");
                    i++;
                }

                try
                {
                    File.Move(ss.FilePath, destPath);

                    if (_settings.EnableBackup && !string.IsNullOrEmpty(_settings.BackupFolder))
                        TryBackup(destPath, match.NameOriginal, false);

                    _processed.Add(ss.FilePath);

                    int current = counts.ContainsKey(match.NameOriginal) ? counts[match.NameOriginal] : 0;
                    counts[match.NameOriginal] = current + 1;

                    _logger.Write(LogType.Move,
                        $"Emulator [{ss.Emulator}]: {Path.GetFileName(ss.FilePath)} → {match.NameOriginal} (Score: {matchResult.Score}%)");
                }
                catch (Exception ex)
                {
                    _logger.Error($"Emulator move failed: {ex.Message}");

                    // ✅ 新增：发送错误通知
                    if (_settings.EnableMismatchNotification)
                    {
                        var fileName = Path.GetFileName(ss.FilePath);
                        var errorKey = $"EMU_ERROR_{ss.Emulator}_{fileName}";
                        bool shouldNotify = !_settings.LimitNotifications || !_notifiedErrors.Contains(errorKey);

                        if (shouldNotify)
                        {
                            var unknownText = _api?.Resources.GetString("LOCGameSnap_GameName_Unknown") ?? "Unknown";
                            var errorTemplate = _api?.Resources.GetString("LOCGameSnap_Error_EmulatorMoveFailed") ??
                                "❌ Error: Emulator ({0}) screenshot ({2}) for game ({1}) failed to organize";
                            var message = string.Format(errorTemplate, ss.Emulator, resolvedName ?? unknownText, fileName);
                            OnFileMoved?.Invoke("GameSnap", message);

                            if (_settings.LimitNotifications)
                            {
                                _notifiedErrors.Add(errorKey);
                            }
                        }
                    }
                }
            }

            if (counts.Count > 0)
            {
                var total = counts.Values.Sum();
                var summary = string.Join(" | ", counts.Select(kv =>
                {
                    var gameName = _currentGame ?? kv.Key;
                    return $"{gameName}→\"{kv.Key}\" ({kv.Value})";
                }));
                var maxScore = matchScores.Values.Max();
                var scoreText = maxScore > 0 ? $"{maxScore}%" : "0%";

                _logger.Info($"Organized: {summary}");

                // ✅ 只有在 NotifyOnEachScreenshot 启用时才发送通知
                // 否则只记录日志，不发送通知
                if (_settings.NotifyOnEachScreenshot)
                {
                    var emulatorsLabel = _api?.Resources.GetString("LOCGameSnap_Summary_Emulators") ?? "Emulators";
                    var screenshotText = _api?.Resources.GetString("LOCGameSnap_Summary_Screenshot") ?? "screenshot(s)";
                    var scorePrefix = _api?.Resources.GetString("LOCGameSnap_Notification_Score") ?? "[Score: {0}]";

                    var message = $"{emulatorsLabel}: {total} {screenshotText}: {summary}  {string.Format(scorePrefix, scoreText)}";
                    OnFileMoved?.Invoke("GameSnap", message);
                }

                OnGamesOrganized?.Invoke(counts.Keys.ToList());
            }
        }

        // ──────────────────────────────────────────────
        // Processa um arquivo individual
        // ──────────────────────────────────────────────
        private void TryOrganizeFile(
            string filePath,
            Dictionary<string, string> dict,
            List<FolderEntry> folders,
            Dictionary<string, int> counts,
            Dictionary<string, int> matchScores)
        {
            var ext = Path.GetExtension(filePath).ToLowerInvariant();
            bool isImage = _settings.ImageExtensions.Contains(ext);
            bool isVideo = _settings.VideoExtensions.Contains(ext);

            if (!isImage && !isVideo) return;

            // Small delay to ensure file is fully written — non-blocking
            System.Threading.Thread.Sleep(800);

            var fileName = Path.GetFileName(filePath);
            var prefix = GetPrefix(fileName);
            var normPfx = DictionaryService.Normalize(prefix);

            // ========== ✅ 新增：黑名单检查 ==========
            // 如果前缀匹配黑名单，直接跳过所有处理（不记录日志，不通知，不移动）
            bool isBlacklisted = _settings.BlacklistPrefixes.Any(p =>
                normPfx.Equals(DictionaryService.Normalize(p), StringComparison.OrdinalIgnoreCase));

            if (isBlacklisted)
            {
                // 静默跳过 - 不记录日志，不通知
                return;
            }
            // ======================================


            // ========== 新增：开始处理日志 ==========
            _logger.Write(LogType.Info, $"═══════════════════════════════════════════════════════════════");
            _logger.Write(LogType.Info, $"📂 Processing: {fileName}");
            _logger.Write(LogType.Info, $"   ├─ Extracted prefix: '{prefix}'");
            _logger.Write(LogType.Info, $"   ├─ Normalized prefix: '{normPfx}'");
            _logger.Write(LogType.Info, $"   ├─ File extension: '{ext}' (Image: {isImage}, Video: {isVideo})");
            _logger.Write(LogType.Info, $"   ├─ Current Playnite game: '{(string.IsNullOrEmpty(_currentGame) ? "NULL" : _currentGame)}'");
            _logger.Write(LogType.Info, $"   ├─ Dictionary entries count: {dict.Count}");
            _logger.Write(LogType.Info, $"   ├─ Available folders count: {folders.Count}");
            _logger.Write(LogType.Info, $"   └─ Window fallback enabled: {_settings.UseWindowFallback}");
            // ======================================

            string? game = null;
            string method = "UNKNOWN";

            // 0. Bypass de emulador
            _logger.Write(LogType.Info, $"🔍 Step 0: Emulator prefix check...");
            bool isEmulatorPrefix = _settings.EmulatorPrefixes
                .Any(p => normPfx.Equals(DictionaryService.Normalize(p), StringComparison.OrdinalIgnoreCase));

            _logger.Write(LogType.Info, $"   └─ Is emulator prefix? {isEmulatorPrefix} (List: {string.Join(", ", _settings.EmulatorPrefixes)})");

            if (isEmulatorPrefix)
            {
                if (!string.IsNullOrEmpty(_currentGame))
                {
                    game = _currentGame;
                    method = "EMULATOR-PLAYNITE";
                    _logger.Write(LogType.Info, $"   └─ ✅ MATCH: Using emulator prefix with Playnite game: '{game}'");
                }
                else
                {
                    _logger.Write(LogType.Error, $"   └─ ❌ Emulator prefix but no active Playnite game");
                    _logger.Write(LogType.Error, $"File: {fileName}\nReason: Emulator prefix '{prefix}' but no active Playnite game");
                    TryMoveToUnmatched(filePath, ext);
                    return;
                }
            }

            // 1. Dicionário
            if (game == null)
            {
                _logger.Write(LogType.Info, $"🔍 Step 1: Dictionary match...");
                bool dictHasKey = dict.TryGetValue(normPfx, out var fromDict);
                _logger.Write(LogType.Info, $"   ├─ Looking for key: '{normPfx}'");

                if (dictHasKey)
                {
                    game = fromDict;
                    method = "DICTIONARY";
                    _logger.Write(LogType.Info, $"   └─ ✅ MATCH: Dictionary found: '{normPfx}' → '{game}'");
                }
                else
                {
                    // 显示字典中的前几个条目供参考
                    var sampleKeys = dict.Keys.Take(5).ToList();
                    _logger.Write(LogType.Info, $"   ├─ Key not found in dictionary");
                    if (sampleKeys.Any())
                    {
                        _logger.Write(LogType.Info, $"   └─ Dictionary sample keys: {string.Join(", ", sampleKeys)}...");
                    }
                    else
                    {
                        _logger.Write(LogType.Info, $"   └─ Dictionary is empty");
                    }
                }
            }

            // 2. Playnite
            if (game == null && _settings.UsePlayniteDetection && !string.IsNullOrEmpty(_currentGame))
            {
                _logger.Write(LogType.Info, $"🔍 Step 2: Playnite current game detection...");
                _logger.Write(LogType.Info, $"   ├─ UsePlayniteDetection: {_settings.UsePlayniteDetection}");
                _logger.Write(LogType.Info, $"   ├─ _currentGame: '{_currentGame}'");
                _logger.Write(LogType.Info, $"   └─ ✅ MATCH: Using Playnite current game: '{_currentGame}'");

                game = _currentGame;
                method = "PLAYNITE";
                if (!string.IsNullOrEmpty(prefix) && prefix.Length > 2)
                {
                    _dictionary.SaveAlias(prefix, _currentGame);
                    _logger.Write(LogType.Learn, $"Prefix: {prefix}\nGame: {_currentGame}");
                }
            }

            // 3. Janela ativa
            bool inGameSession = _currentGame != null;
            bool prefixKnown = dict.ContainsKey(normPfx);
            bool canUseFallback = _settings.UseWindowFallback && (inGameSession || prefixKnown);

            _logger.Write(LogType.Info, $"🔍 Step 3: Window fallback check...");
            _logger.Write(LogType.Info, $"   ├─ inGameSession: {inGameSession} (_currentGame = '{_currentGame}')");
            _logger.Write(LogType.Info, $"   ├─ prefixKnown: {prefixKnown} (key exists in dictionary)");
            _logger.Write(LogType.Info, $"   ├─ canUseFallback: {canUseFallback} (UseWindowFallback={_settings.UseWindowFallback})");

            if (game == null && canUseFallback)
            {
                _logger.Write(LogType.Info, $"   ├─ Attempting to get active window title...");
                var win = GetActiveWindowTitle();
                _logger.Write(LogType.Info, $"   ├─ Raw window title: '{win}'");

                if (!string.IsNullOrEmpty(win) && win.Length > 4)
                {
                    var normWin = DictionaryService.Normalize(win);
                    _logger.Write(LogType.Info, $"   ├─ Normalized window title: '{normWin}'");

                    bool blocked = _settings.WindowBlacklist.Any(b =>
                        normWin.IndexOf(b, StringComparison.OrdinalIgnoreCase) >= 0);

                    bool looksLikeSystem =
                        normWin.Contains("explorador de arquivos") ||
                        normWin.Contains("file explorer") ||
                        normWin.Contains("mais guias") ||
                        normWin.Contains("more tabs") ||
                        normWin.Contains("google drive") ||
                        normWin.Contains("onedrive") ||
                        normWin.Contains("hotmail") ||
                        normWin.Contains("playnite") ||
                        normWin.Contains("gmail") ||
                        normWin.Contains("outlook") ||
                        normWin.Contains(" - explorador") ||
                        normWin.Contains(" - explorer") ||
                        normWin.Length < 3;

                    _logger.Write(LogType.Info, $"   ├─ Blocked by blacklist: {blocked}");
                    _logger.Write(LogType.Info, $"   ├─ Looks like system window: {looksLikeSystem}");

                    if (blocked)
                    {
                        var matchedBlacklist = _settings.WindowBlacklist.FirstOrDefault(b =>
                            normWin.IndexOf(b, StringComparison.OrdinalIgnoreCase) >= 0);
                        _logger.Write(LogType.Info, $"   └─ ⛔ Blocked by blacklist entry: '{matchedBlacklist}'");
                    }
                    else if (looksLikeSystem)
                    {
                        _logger.Write(LogType.Info, $"   └─ ⛔ Blocked: Looks like system window");
                    }
                    else if (!blocked && !looksLikeSystem)
                    {
                        game = win;
                        method = "WINDOW";
                        _logger.Write(LogType.Info, $"   └─ ✅ MATCH: Using window title: '{game}'");
                        _logger.Write(LogType.Fallback, $"Prefix: {prefix}\nDetected: {win}");
                    }
                }
                else
                {
                    _logger.Write(LogType.Info, $"   └─ ⛔ Window title empty or too short (length: {win?.Length ?? 0})");
                }
            }
            else
            {
                if (game != null)
                {
                    _logger.Write(LogType.Info, $"   └─ Skipping: Game already found in previous step");
                }
                else if (!canUseFallback)
                {
                    _logger.Write(LogType.Info, $"   └─ Skipping: Window fallback not allowed");
                }
            }

            // Sem match
            if (game == null)
            {
                _logger.Write(LogType.Error, $"❌ No match after all steps");
                _logger.Write(LogType.Error, $"File: {fileName}\nReason: No detection");
                TryMoveToUnmatched(filePath, ext);
                return;
            }

            _logger.Write(LogType.Info, $"✅ Game resolved: '{game}' (Method: {method})");

            // ─── Step 4: 文件夹匹配 ──────────────────────────────────
            _logger.Write(LogType.Info, $"🔍 Step 4: Folder matching...");
            _logger.Write(LogType.Info, $"   ├─ Resolved game name (for folder selection): '{game}'");
            _logger.Write(LogType.Info, $"   ├─ Current Playnite game (for score): '{_currentGame ?? "NULL"}'");
            _logger.Write(LogType.Info, $"   ├─ ForceCreateFolder: {_settings.ForceCreateFolder}");
            _logger.Write(LogType.Info, $"   ├─ ForceCreateOnScreenshot: {_settings.ForceCreateOnScreenshot}");
            _logger.Write(LogType.Info, $"   ├─ Available folders:");

            // 列出所有可用文件夹
            foreach (var f in folders)
            {
                _logger.Write(LogType.Info, $"   │   ├─ '{f.NameOriginal}' (norm: '{f.NameNorm}')");
            }

            FolderEntry? match = null;
            int matchScore = 0;
            string matchType = "none";
            // bool forceCreated = false;

            // 确定要使用的目标文件夹名称（优先使用字典解析结果）
            string targetGameForFolder = game ?? _currentGame ?? "UNKNOWN";

            // 1. 尝试精确匹配
            if (!string.IsNullOrEmpty(targetGameForFolder))
            {
                var exactMatch = folders.FirstOrDefault(f =>
                    string.Equals(f.NameOriginal, targetGameForFolder, StringComparison.OrdinalIgnoreCase));

                if (exactMatch != null)
                {
                    match = exactMatch;
                    matchType = method == "DICTIONARY" ? "dictionary_exact" : "exact_match";
                    _logger.Write(LogType.Info, $"   ├─ ✅ Exact folder found: '{match.NameOriginal}'");
                }
            }

            // 2. 如果精确匹配失败且 ForceCreateFolder 启用，尝试创建文件夹
            if (match == null && _settings.ForceCreateFolder && _settings.ForceCreateOnScreenshot)
            {
                var folderPath = CreateFolderForGame(targetGameForFolder);
                if (!string.IsNullOrEmpty(folderPath))
                {
                    match = new FolderEntry
                    {
                        NameOriginal = Path.GetFileName(folderPath),
                        NameNorm = DictionaryService.Normalize(Path.GetFileName(folderPath)),
                        Path = folderPath
                    };
                    matchType = method == "DICTIONARY" ? "dictionary_force_create" : "force_create";
                    _logger.Write(LogType.Info, $"   ├─ ✅ Created folder: '{match.NameOriginal}'");
                }
            }

            // 3. ✅ 修改：如果精确匹配和强制创建都失败，直接判定失败
            //    不再使用相似度匹配，不再保底创建
            if (match == null)
            {
                _logger.Write(LogType.Error, $"   └─ ❌ No folder found (exact match failed and force create disabled or failed)");
                _logger.Write(LogType.Error, $"File: {fileName}\nGame: {targetGameForFolder}\nNo folder found");

                // ✅ 发送文件夹不存在的错误通知
                if (_settings.EnableMismatchNotification)
                {
                    var folderName = SanitizeFileName(targetGameForFolder);
                    var errorKey = $"FOLDER_ERROR_{targetGameForFolder}_{fileName}";
                    bool shouldNotify = !_settings.LimitNotifications || !_notifiedFolderErrors.Contains(errorKey);

                    if (shouldNotify)
                    {
                        var unknownText = _api?.Resources.GetString("LOCGameSnap_GameName_Unknown") ?? "Unknown";
                        var errorTemplate = _api?.Resources.GetString("LOCGameSnap_Error_FolderNotFound") ??
                            "❌ Error: Screenshot ({1}) for game ({0}) failed to organize to folder ({2}), please check if the folder exists";
                        var gameName = _currentGame ?? unknownText;
                        var errorMsg = string.Format(errorTemplate, gameName, fileName, folderName);

                        OnFileMoved?.Invoke("GameSnap", errorMsg);

                        if (_settings.LimitNotifications)
                        {
                            _notifiedFolderErrors.Add(errorKey);
                        }
                    }
                }

                TryMoveToUnmatched(filePath, ext);
                return;
            }

            // 计算实际相似度（用于显示）- 始终基于当前游戏名计算
            // ✅ 保留：用于通知中显示相似度
            if (_currentGame != null)
            {
                var tempResult = FindBestMatchByScore(_currentGame, new List<FolderEntry> { match }, 0);
                matchScore = tempResult.Score > 0 ? tempResult.Score : 0;
            }
            else
            {
                matchScore = 0;
            }

            _logger.Write(LogType.Info, $"   └─ ✅ Final folder: '{match.NameOriginal}' (Score: {matchScore}%, Type: {matchType})");

            // Record match score
            if (!matchScores.ContainsKey(match.NameOriginal) || matchScore > matchScores[match.NameOriginal])
            {
                matchScores[match.NameOriginal] = matchScore;
            }

            // Destino final
            var destDir = isVideo
                ? EnsureDir(Path.Combine(match.Path, "Videos"))
                : match.Path;

            var date = GetBestDate(filePath);
            var destName = BuildDestName(match.NameOriginal, date, Path.GetFileNameWithoutExtension(fileName), ext);
            var destPath = Path.Combine(destDir, destName);

            _logger.Write(LogType.Info, $"📁 Destination: '{destPath}'");

            int i = 1;
            while (File.Exists(destPath))
            {
                var nameNoExt = Path.GetFileNameWithoutExtension(destName);
                destPath = Path.Combine(destDir, $"{nameNoExt}_{i}{ext}");
                i++;
            }

            try
            {
                File.Move(filePath, destPath);
                _logger.Write(LogType.Info, $"✅ File moved successfully");

                if (_settings.EnableBackup && !string.IsNullOrEmpty(_settings.BackupFolder))
                    TryBackup(destPath, match.NameOriginal, isVideo);

                // _processed.Add(filePath);

                int current = counts.ContainsKey(match.NameOriginal) ? counts[match.NameOriginal] : 0;
                counts[match.NameOriginal] = current + 1;

                // ✅ 新增：检查游戏名是否匹配
                if (_settings.EnableMismatchNotification && !string.IsNullOrEmpty(_currentGame))
                {
                    // 规范化比较（移除非法字符，与文件夹匹配逻辑一致）
                    var normalizedCurrent = DictionaryService.Normalize(_currentGame);
                    var normalizedFolder = DictionaryService.Normalize(match.NameOriginal);

                    // 如果当前游戏名与目标文件夹名不匹配（不区分大小写，忽略特殊字符）
                    if (!string.Equals(normalizedCurrent, normalizedFolder, StringComparison.OrdinalIgnoreCase))
                    {
                        // 检查是否应该通知（限制通知逻辑）
                        bool shouldNotify = !_settings.LimitNotifications ||
                                            !_notifiedMismatches.Contains(normalizedCurrent + "|" + normalizedFolder);

                        if (shouldNotify)
                        {
                            // 发送通知
                            var warningTemplate = _api?.Resources.GetString("LOCGameSnap_Warning_Mismatch") ??
                                "⚠️ Warning: Current game ({0}) does not match screenshot folder ({1})";
                            var message = string.Format(warningTemplate, _currentGame ?? "", match.NameOriginal);
                            OnFileMoved?.Invoke("GameSnap", message);

                            // 记录已通知
                            if (_settings.LimitNotifications)
                            {
                                _notifiedMismatches.Add(normalizedCurrent + "|" + normalizedFolder);
                            }
                        }
                    }
                }

                _logger.Write(LogType.Move, $"File: {fileName}\nGame: {game}\nMethod: {method}\nScore: {matchScore}% ({matchType})");
                _logger.Write(LogType.Info, $"═══════════════════════════════════════════════════════════════");
            }
            catch (Exception ex)
            {
                // ✅ 修改：捕获移动失败的异常，发送错误通知
                _logger.Write(LogType.Error, $"File: {fileName}\nMove failed: {ex.Message}");
                _logger.Write(LogType.Info, $"═══════════════════════════════════════════════════════════════");

                // ✅ 新增：发送错误通知（按文件路径去重）
                if (_settings.EnableMismatchNotification)
                {
                    // 使用文件路径作为唯一标识，确保每个文件只通知一次
                    var errorKey = $"MOVE_ERROR_{filePath}";
                    bool shouldNotify = !_settings.LimitNotifications || !_notifiedMoveErrors.Contains(errorKey);

                    if (shouldNotify)
                    {
                        var unknownText = _api?.Resources.GetString("LOCGameSnap_GameName_Unknown") ?? "Unknown";
                        var errorTemplate = _api?.Resources.GetString("LOCGameSnap_Error_MoveFailed") ??
                            "❌ Error: Screenshot ({1}) for game ({0}) failed to move to folder ({2})";
                        var gameName = _currentGame ?? unknownText;
                        var errorMsg = string.Format(errorTemplate, gameName, fileName, match.NameOriginal);

                        if (_settings.LimitNotifications)
                        {
                            _notifiedMoveErrors.Add(errorKey);
                        }
                    }
                }
            }
        }

        // ──────────────────────────────────────────────
        // Pasta Unmatched
        // ──────────────────────────────────────────────
        private void TryMoveToUnmatched(string filePath, string ext)
        {
            if (!_settings.MoveUnmatchedToFolder) return;
            if (string.IsNullOrWhiteSpace(_settings.DestinationBase)) return;

            try
            {
                var unmatchedDir = EnsureDir(
                    Path.Combine(_settings.DestinationBase, _settings.UnmatchedFolderName));

                var destPath = Path.Combine(unmatchedDir, Path.GetFileName(filePath));
                int i = 1;
                while (File.Exists(destPath))
                {
                    var nameNoExt = Path.GetFileNameWithoutExtension(filePath);
                    destPath = Path.Combine(unmatchedDir, $"{nameNoExt}_{i}{ext}");
                    i++;
                }

                File.Move(filePath, destPath);
                // ✅ 移除：不再需要这里添加 _processed，因为已经在开头添加了
                // _processed.Add(filePath);
                _logger.Write(LogType.Info, $"Moved to unmatched: {Path.GetFileName(filePath)}");
            }
            catch (Exception ex)
            {
                _logger.Write(LogType.Error, $"Unmatched move failed: {ex.Message}");
            }
        }

        // ──────────────────────────────────────────────
        // Backup
        // ──────────────────────────────────────────────
        private void TryBackup(string sourcePath, string gameName, bool isVideo)
        {
            try
            {
                var backupGame = EnsureDir(Path.Combine(_settings.BackupFolder, gameName));
                var backupDir = isVideo ? EnsureDir(Path.Combine(backupGame, "Videos")) : backupGame;
                var destPath = Path.Combine(backupDir, Path.GetFileName(sourcePath));

                if (!File.Exists(destPath))
                    File.Copy(sourcePath, destPath);
            }
            catch (Exception ex)
            {
                _logger.Write(LogType.Error, $"Backup failed: {ex.Message}");
            }
        }

        // ──────────────────────────────────────────────
        // Renomeação customizável
        // ──────────────────────────────────────────────
        private string BuildDestName(string gameName, DateTime date, string originalName, string ext)
        {
            var pattern = string.IsNullOrWhiteSpace(_settings.RenamePattern)
                ? "{game}_{date}_{time}"
                : _settings.RenamePattern;

            var result = pattern
                .Replace("{game}", SanitizeFileName(gameName))
                .Replace("{date}", date.ToString("yyyy-MM-dd"))
                .Replace("{time}", date.ToString("HH_mm_ss"))
                .Replace("{datetime}", date.ToString("yyyy-MM-dd_HH_mm_ss"))
                .Replace("{original}", SanitizeFileName(originalName));

            return result + ext;
        }

        private static string SanitizeFileName(string name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            return string.Concat(name.Split(invalid)).Trim();
        }

        // ──────────────────────────────────────────────
        // 创建游戏文件夹
        // ──────────────────────────────────────────────
        private string? CreateFolderForGame(string gameName)
        {
            if (string.IsNullOrWhiteSpace(_settings.DestinationBase)) return null;
            if (!Directory.Exists(_settings.DestinationBase)) return null;

            var folderName = SanitizeFileName(gameName);
            if (string.IsNullOrWhiteSpace(folderName)) return null;

            var folderPath = Path.Combine(_settings.DestinationBase, folderName);

            try
            {
                if (!Directory.Exists(folderPath))
                {
                    Directory.CreateDirectory(folderPath);
                    _logger.Write(LogType.Info, $"📁 Created folder: {folderPath}");
                }
                else
                {
                    _logger.Write(LogType.Info, $"📁 Folder already exists: {folderPath}");
                }
                return folderPath;
            }
            catch (Exception ex)
            {
                _logger.Write(LogType.Error, $"Failed to create folder: {ex.Message}");
                return null;
            }
        }

        // ──────────────────────────────────────────────
        // Helpers
        // ──────────────────────────────────────────────

        private List<FolderEntry> LoadFolders()
        {
            var folders = Directory.GetDirectories(_settings.DestinationBase)
                .Select(d => new FolderEntry
                {
                    NameOriginal = Path.GetFileName(d),
                    NameNorm = DictionaryService.Normalize(Path.GetFileName(d)),
                    Path = d
                })
                .ToList();

            _logger.Write(LogType.Info, $"📁 Loaded {folders.Count} folders from: {_settings.DestinationBase}");
            foreach (var f in folders)
            {
                _logger.Write(LogType.Info, $"   ├─ '{f.NameOriginal}'");
            }

            return folders;
        }

        private static string GetPrefix(string filename)
        {
            var m = Regex.Match(filename, @"^([^_]+)_");
            return m.Success
                ? m.Groups[1].Value
                : Path.GetFileNameWithoutExtension(filename);
        }

        private static DateTime GetBestDate(string filePath)
        {
            var name = Path.GetFileNameWithoutExtension(filePath);
            var m = Regex.Match(name, @"(\d{4})[-_](\d{2})[-_](\d{2}).*?(\d{2})[-_](\d{2})[-_](\d{2})");
            if (m.Success)
            {
                try
                {
                    return new DateTime(
                        int.Parse(m.Groups[1].Value),
                        int.Parse(m.Groups[2].Value),
                        int.Parse(m.Groups[3].Value),
                        int.Parse(m.Groups[4].Value),
                        int.Parse(m.Groups[5].Value),
                        int.Parse(m.Groups[6].Value));
                }
                catch { }
            }

            var info = new FileInfo(filePath);
            return info.LastWriteTime != default ? info.LastWriteTime : info.CreationTime;
        }

        private static string EnsureDir(string path)
        {
            if (!Directory.Exists(path))
                Directory.CreateDirectory(path);
            return path;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);

        private static string GetActiveWindowTitle()
        {
            var sb = new StringBuilder(256);
            GetWindowText(GetForegroundWindow(), sb, sb.Capacity);
            return sb.ToString();
        }

        // ──────────────────────────────────────────────
        // Inner classes
        // ──────────────────────────────────────────────

        private class FolderEntry
        {
            public string NameOriginal { get; set; } = "";
            public string NameNorm { get; set; } = "";
            public string Path { get; set; } = "";
        }

        private class MatchResult
        {
            public FolderEntry? Folder { get; set; }
            public int Score { get; set; }
            public string MatchType { get; set; } = "none";
            public bool IsMatch => Folder != null && Score > 0;
        }

        // ──────────────────────────────────────────────
        // Similarity score matching
        // ──────────────────────────────────────────────

        private MatchResult FindBestMatchByScore(string gameName, List<FolderEntry> folders, int minScore = 50)
        {
            if (string.IsNullOrEmpty(gameName) || folders.Count == 0)
                return new MatchResult { Score = 0, MatchType = "none" };

            var normGame = DictionaryService.Normalize(gameName);
            _logger.Write(LogType.Info, $"   ├─ Comparing against normalized game: '{normGame}'");

            var candidates = new List<(FolderEntry Folder, int Score, string MatchType)>();

            int folderIndex = 0;
            foreach (var folder in folders)
            {
                folderIndex++;
                int score = 0;
                string matchType = "none";
                var normFolder = folder.NameNorm;

                _logger.Write(LogType.Info, $"   │   [{folderIndex}/{folders.Count}] Checking folder: '{folder.NameOriginal}' (norm: '{normFolder}')");

                // ─── 1. Exact match (100 points) ───
                if (string.Equals(normFolder, normGame, StringComparison.OrdinalIgnoreCase))
                {
                    score = 100;
                    matchType = "exact";
                    _logger.Write(LogType.Info, $"   │       ├─ ✅ EXACT MATCH: '{normFolder}' == '{normGame}' → score=100");
                    candidates.Add((folder, score, matchType));
                    continue;
                }

                // ─── 2. Containment match ───
                if (normFolder.Contains(normGame))
                {
                    var ratio = (double)normGame.Length / normFolder.Length;
                    score = 60 + (int)(ratio * 20);
                    matchType = "folder_contains_game";
                    _logger.Write(LogType.Info, $"   │       ├─ ✅ CONTAINS: '{normFolder}' contains '{normGame}' → score={score}");
                }
                else if (normGame.Contains(normFolder))
                {
                    var ratio = (double)normFolder.Length / normGame.Length;
                    score = 50 + (int)(ratio * 30);
                    matchType = "game_contains_folder";
                    _logger.Write(LogType.Info, $"   │       ├─ ✅ CONTAINS: '{normGame}' contains '{normFolder}' → score={score}");
                }

                // ─── 3. Word match ───
                var gameWords = normGame.Split(new[] { ' ', '-', '_', '.' }, StringSplitOptions.RemoveEmptyEntries);
                var folderWords = normFolder.Split(new[] { ' ', '-', '_', '.' }, StringSplitOptions.RemoveEmptyEntries);

                if (gameWords.Length > 0 && folderWords.Length > 0)
                {
                    var commonWords = gameWords.Intersect(folderWords, StringComparer.OrdinalIgnoreCase).ToList();
                    var commonCount = commonWords.Count;

                    if (commonCount > 0)
                    {
                        var maxWords = Math.Max(gameWords.Length, folderWords.Length);
                        var wordScore = (int)((double)commonCount / maxWords * 40);

                        _logger.Write(LogType.Info, $"   │       ├─ WORD MATCH: Common words: [{string.Join(", ", commonWords)}] (count={commonCount}/{maxWords}) → base score={wordScore}");

                        if (score > 0)
                        {
                            var oldScore = score;
                            score = Math.Min(score + wordScore / 2, 95);
                            _logger.Write(LogType.Info, $"   │       │    └─ Combined: {oldScore} + {wordScore/2} = {score}");
                        }
                        else
                        {
                            score = 30 + wordScore;
                            matchType = "word_match";
                            _logger.Write(LogType.Info, $"   │       ├─ WORD MATCH: {commonCount} common words → score={score}");
                        }
                    }
                    else
                    {
                        _logger.Write(LogType.Info, $"   │       ├─ No common words found");
                    }
                }

                // ─── 4. Prefix match ───
                if (normGame.StartsWith(normFolder, StringComparison.OrdinalIgnoreCase) ||
                    normFolder.StartsWith(normGame, StringComparison.OrdinalIgnoreCase))
                {
                    if (score < 70)
                    {
                        score = 70;
                        matchType = "prefix_match";
                        _logger.Write(LogType.Info, $"   │       ├─ PREFIX MATCH: Set score to 70");
                    }
                }

                if (score > 0)
                {
                    candidates.Add((folder, score, matchType));
                    _logger.Write(LogType.Info, $"   │       └─ ✅ Added to candidates: {folder.NameOriginal} (Score: {score}, Type: {matchType})");
                }
                else
                {
                    _logger.Write(LogType.Info, $"   │       └─ ⛔ No match (score=0)");
                }
            }

            // ─── 5. Sort by score descending ───
            _logger.Write(LogType.Info, $"   ├─ Candidates found: {candidates.Count}");

            var best = candidates
                .OrderByDescending(c => c.Score)
                .FirstOrDefault();

            if (best.Folder == null || best.Score < minScore)
            {
                if (best.Folder == null)
                {
                    _logger.Write(LogType.Info, $"   └─ No match found for '{gameName}'");
                }
                else
                {
                    _logger.Write(LogType.Info, $"   └─ Match score {best.Score} below threshold {minScore} for '{gameName}' → no match");
                }
                return new MatchResult { Score = best.Score, MatchType = best.MatchType };
            }

            _logger.Write(LogType.Info, $"   └─ ✅ BEST MATCH: '{best.Folder.NameOriginal}' (Score: {best.Score}, Type: {best.MatchType})");

            // Check for candidates with close scores (difference <= 5)
            var closeCandidates = candidates
                .Where(c => c.Score >= best.Score - 5 && c.Folder != best.Folder)
                .ToList();
            if (closeCandidates.Any())
            {
                var others = string.Join(", ", closeCandidates.Select(c => $"{c.Folder.NameOriginal}({c.Score})"));
                _logger.Write(LogType.Info, $"   └─ ⚠️ Multiple close matches: {others}. Selected: {best.Folder.NameOriginal}");
            }

            return new MatchResult
            {
                Folder = best.Folder,
                Score = best.Score,
                MatchType = best.MatchType
            };
        }
    }
}