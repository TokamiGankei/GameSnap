using Playnite.SDK;
using Playnite.SDK.Events;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Controls;

namespace GameSnapPlugin
{
    public class GameSnapPlugin : GenericPlugin
    {
        public override Guid Id { get; } = Guid.Parse("1826881c-4e6e-4ed3-ac6c-8605f953daf4");

        // ScreenshotsVisualizer GUID — usado para o refresh automático
        private static readonly Guid ScreenshotsVisualizerId = Guid.Parse("c6c8276f-91bf-48e5-a1d1-4bee0b493488");

        public GameSnapSettingsViewModel PluginSettings { get; private set; }
        private GameSnapSettings S => PluginSettings.Settings;

        private GameSnapLogger?    _logger;
        private DictionaryService? _dict;
        private OrganizerService?  _organizer;
        private WatcherService?    _watcher;
        private SteamService?      _steam;
        private EmulatorService?   _emulator;

        // ✅ 新增：跟踪当前游戏会话中是否有截图被整理
        private bool _hasOrganizedInSession = false;

        // ✅ 新增：存储当前游戏会话中整理的截图数量（用于汇总通知）
        private int _sessionScreenshotCount = 0;
        private string? _sessionGameName = null;

        public GameSnapPlugin(IPlayniteAPI api) : base(api)
        {
            Properties = new GenericPluginProperties { HasSettings = true };
            PluginSettings = new GameSnapSettingsViewModel(this);
        }

        public override void OnApplicationStarted(OnApplicationStartedEventArgs args)
        {
            System.Threading.Tasks.Task.Run(() =>
            {
                try   { InitServices(S); _watcher?.Start(); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"GameSnap init error: {ex.Message}"); }
            });
        }

        public override void OnApplicationStopped(OnApplicationStoppedEventArgs args)
        {
            _watcher?.Stop();
            _watcher?.Dispose();
        }

        public override void OnGameStarted(OnGameStartedEventArgs args)
        {
            _organizer?.SetCurrentGame(args.Game.Name);
            // ✅ 新增：重置会话跟踪
            _hasOrganizedInSession = false;
            _sessionScreenshotCount = 0;
            _sessionGameName = args.Game.Name;

            // ✅ 修改：根据 ForceCreateOnGameStart 决定是否创建文件夹
            if (S.ForceCreateFolder && S.ForceCreateOnGameStart)
            {
                CreateFolderForGame(args.Game.Name);
            }
        }

        public override void OnGameStopped(OnGameStoppedEventArgs args)
        {
            _organizer?.SetCurrentGame(null);
            // ✅ 新增：游戏结束时，如果启用"游戏结束后"通知且有截图被整理，发送汇总通知
            if (S.NotifyOnGameEnd && _hasOrganizedInSession)
            {
                SendEndOfGameNotification(args.Game.Name);
            }

            // ✅ 重置会话状态
            _hasOrganizedInSession = false;
            _sessionScreenshotCount = 0;
            _sessionGameName = null;
        }

        // ✅ 新增：发送游戏结束时的汇总通知
        private void SendEndOfGameNotification(string gameName)
        {
            try
            {
                var gameEndTemplate = PlayniteApi.Resources.GetString("LOCGameSnap_Notification_GameEnd") ??
                    "🎮 Game session ended, organized {1} screenshot(s) for \"{0}\"";
                var message = string.Format(gameEndTemplate, gameName, _sessionScreenshotCount);

                PlayniteApi.Notifications.Add(
                    new NotificationMessage(
                        Guid.NewGuid().ToString(),
                        message,
                        NotificationType.Info
                    )
                );

                _logger?.Info($"End of game notification sent: {message}");
            }
            catch (Exception ex)
            {
                _logger?.Error($"Failed to send end of game notification: {ex.Message}");
            }
        }

        private void TryAutoCreateFolder(string gameName)
        {
            // 使用 ForceCreateFolder 替代 AutoCreateFolders
            if (!S.ForceCreateFolder) return;
            if (string.IsNullOrWhiteSpace(S.DestinationBase)) return;
            if (!Directory.Exists(S.DestinationBase)) return;

            var folderPath = CreateFolderForGame(gameName);
            if (folderPath != null)
            {
                _logger?.Info($"Auto-created folder: {folderPath}");
            }
        }

        // 添加创建文件夹的辅助方法
        private string? CreateFolderForGame(string gameName)
        {
            if (string.IsNullOrWhiteSpace(S.DestinationBase)) return null;
            if (!Directory.Exists(S.DestinationBase)) return null;

            var invalid = Path.GetInvalidFileNameChars();
            var folderName = string.Concat(gameName.Split(invalid)).Trim();
            if (string.IsNullOrWhiteSpace(folderName)) return null;

            var folderPath = Path.Combine(S.DestinationBase, folderName);

            try
            {
                if (!Directory.Exists(folderPath))
                {
                    Directory.CreateDirectory(folderPath);
                }
                return folderPath;
            }
            catch
            {
                return null;
            }
        }

        // ── ScreenshotsVisualizer integration ───────────────────────────────────

        // Notifica o ScreenshotsVisualizer para reescanear um jogo após mover screenshots.
        // Usa reflexão para não criar dependência direta no projeto.
        public void NotifyScreenshotsVisualizerRefresh(Game game)
        {
            try
            {
                var sv = PlayniteApi.Addons.Plugins
                    .FirstOrDefault(p => p.Id == ScreenshotsVisualizerId);
                if (sv == null)
                {
                    _logger?.Info("ScreenshotsVisualizer plugin not found");
                    return;
                }

                // 直接调用 SV 的 RefreshGame 方法
                var method = sv.GetType().GetMethod("RefreshGame",
                    BindingFlags.Public | BindingFlags.Instance);
                if (method != null)
                {
                    method.Invoke(sv, new object[] { game });
                    _logger?.Info($"ScreenshotsVisualizer refreshed for: {game.Name}");
                }
                else
                {
                    _logger?.Info("RefreshGame method not found in SV");
                }
            }
            catch (Exception ex)
            {
                // Silencioso — SV pode não estar instalado
                _logger?.Info($"ScreenshotsVisualizer refresh skipped: {ex.Message}");
            }
        }

        // ── Settings ─────────────────────────────────────────────────────────────

        public override ISettings GetSettings(bool firstRunSettings) => PluginSettings;

        public override UserControl GetSettingsView(bool firstRunSettings)
            => new Views.SettingsTabView();

        public void ApplySettings(GameSnapSettings s)
        {
            // ✅ 保存当前游戏状态
            string? currentGameName = _organizer != null ? _organizer.GetCurrentGame() : null;

            _watcher?.Stop();
            _watcher?.Dispose();
            _watcher = null;
            InitServices(s);

            // ✅ 恢复当前游戏状态
            if (!string.IsNullOrEmpty(currentGameName))
            {
                _organizer?.SetCurrentGame(currentGameName);
            }

            _watcher?.Start();
        }

        // ── Services ─────────────────────────────────────────────────────────────

        private void InitServices(GameSnapSettings s)
        {
            var dataPath = GetPluginUserDataPath();
            Directory.CreateDirectory(dataPath);

            _logger    = new GameSnapLogger(dataPath);
            _dict      = new DictionaryService(dataPath);
            _organizer = new OrganizerService(s, _dict, _logger, PlayniteApi);  // ✅ 传递 API

            if (s.EnableSteamSupport)
            {
                _steam = new SteamService(PlayniteApi, _logger);
                _organizer.SteamService = _steam;
            }
            else
            {
                _steam = null;
                _organizer.SteamService = null;
            }

            if (s.EnableEmulatorSupport)
            {
                _emulator = new EmulatorService(PlayniteApi, s, _logger);
                _organizer.EmulatorService = _emulator;
            }
            else
            {
                _emulator = null;
                _organizer.EmulatorService = null;
            }

            _organizer.OnFileMoved = (summary, message) =>
            {
                // ✅ 新增：如果是汇总通知（以"Organized"开头），记录会话统计
                if (message.StartsWith("Organized") && _organizer != null)
                {
                    // 提取截图数量
                    var match = System.Text.RegularExpressions.Regex.Match(message, @"Organized (\d+) screenshot");
                    if (match.Success && int.TryParse(match.Groups[1].Value, out int count))
                    {
                        _sessionScreenshotCount += count;
                        _hasOrganizedInSession = true;
                    }
                }

                // ✅ 判断是否是异常通知（警告或错误）
                bool isErrorOrWarning = message.StartsWith("⚠️") || message.StartsWith("❌") ||
                                        message.Contains("警告") || message.Contains("错误");

                // ✅ 如果是异常通知，检查 EnableMismatchNotification
                if (isErrorOrWarning)
                {
                    if (!s.EnableMismatchNotification) return;
                    // 异常通知不受 ShowNotifications 和 NotifyOnEachScreenshot 限制
                    // 直接发送
                }
                else
                {
                    // ✅ 普通通知（汇总通知）- 受 ShowNotifications 和 NotifyOnEachScreenshot 控制
                    if (!s.NotifyOnEachScreenshot) return;
                    if (!s.ShowNotifications) return;
                }

                // 确定通知类型
                NotificationType type = isErrorOrWarning ? NotificationType.Error : NotificationType.Info;

                PlayniteApi.Notifications.Add(
                    new NotificationMessage(Guid.NewGuid().ToString(), message, type)
                );
            };

            _organizer.OnGamesOrganized = (gameNames) =>
            {
                if (!s.EnableScreenshotsVisualizerRefresh) return;

                // Notifica o ScreenshotsVisualizer para reescanear cada jogo afetado
                foreach (var name in gameNames)
                {
                    var game = PlayniteApi.Database.Games
                        .FirstOrDefault(g => g.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                    if (game != null)
                        NotifyScreenshotsVisualizerRefresh(game);
                }
            };

            _watcher = new WatcherService(s, _organizer, _logger);
        }

        // ── Menus ─────────────────────────────────────────────────────────────────

        public override IEnumerable<GameMenuItem> GetGameMenuItems(GetGameMenuItemsArgs args)
        {
            yield return new GameMenuItem
            {
                Description = PlayniteApi.Resources.GetString("LOCGameSnapOrganizeScreenshots"),
                MenuSection = "GameSnap",
                Action = _ => _organizer?.Organize()
            };
        }

        public override IEnumerable<MainMenuItem> GetMainMenuItems(GetMainMenuItemsArgs args)
        {
            var loc = PlayniteApi.Resources;

            yield return new MainMenuItem
            {
                Description = loc.GetString("LOCGameSnapOrganizeScreenshots"),
                MenuSection = "@GameSnap",
                Action = _ => _organizer?.Organize()
            };
            yield return new MainMenuItem
            {
                Description = loc.GetString("LOCGameSnapOpenLog"),
                MenuSection = "@GameSnap",
                Action = _ =>
                {
                    var path = Path.Combine(GetPluginUserDataPath(), "gamesnap.log");
                    if (File.Exists(path))
                        System.Diagnostics.Process.Start(
                            new System.Diagnostics.ProcessStartInfo("notepad.exe", path) { UseShellExecute = true });
                }
            };
            yield return new MainMenuItem
            {
                Description = loc.GetString("LOCGameSnapOpenDictionary"),
                MenuSection = "@GameSnap",
                Action = _ =>
                {
                    var path = Path.Combine(GetPluginUserDataPath(), "dictionary.txt");
                    if (!File.Exists(path))
                        File.WriteAllText(path, "# Format:\n# [Game Name]\n# alias1\n");
                    System.Diagnostics.Process.Start(
                        new System.Diagnostics.ProcessStartInfo("notepad.exe", path) { UseShellExecute = true });
                }
            };
            yield return new MainMenuItem
            {
                Description = loc.GetString("LOCGameSnapReviewUnmatched"),
                MenuSection = "@GameSnap",
                Action = _ => OpenReviewWindow()
            };
            yield return new MainMenuItem
            {
                Description = loc.GetString("LOCGameSnapReviewUnmatchedFullscreen"),
                MenuSection = "@GameSnap",
                Action = _ => OpenFullscreenReviewWindow()
            };
        }

        private void OpenReviewWindow()
        {
            if (_organizer == null || _dict == null || _logger == null)
            {
                PlayniteApi.Dialogs.ShowMessage(
                    PlayniteApi.Resources.GetString("LOCGameSnap_NotInitialized") ?? "GameSnap is not fully initialized.",
                    "GameSnap");
                return;
            }
            var vm     = new ReviewViewModel(PlayniteApi, S, _dict, _organizer, _logger);
            var window = new Views.ReviewWindow(vm);
            vm.SetCloseAction(() => window.Close());
            window.ShowDialog();
        }

        private void OpenFullscreenReviewWindow()
        {
            if (_organizer == null || _dict == null || _logger == null)
            {
                PlayniteApi.Dialogs.ShowMessage(
                    PlayniteApi.Resources.GetString("LOCGameSnap_NotInitialized") ?? "GameSnap is not fully initialized.",
                    "GameSnap");
                return;
            }
            var window = new Views.FullscreenReviewWindow(
                PlayniteApi, S, _dict, _organizer, _logger);
            window.ShowDialog();
        }
    }
}
