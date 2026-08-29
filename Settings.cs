using Playnite.SDK;
using Playnite.SDK.Data;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;

namespace GameSnapPlugin
{
    // Dados puros — igual ao ScreenshotsVisualizerSettings.
    // IMPORTANTE: listas iniciam VAZIAS para evitar duplicatas na desserializacao.
    // O ViewModel preenche com defaults se o JSON nao tiver dados.
    public class GameSnapSettings : ObservableObject
    {
        private string _sourceFolder = "";
        public string SourceFolder { get => _sourceFolder; set => SetValue(ref _sourceFolder, value); }

        private List<string> _additionalSourceFolders = new List<string>();
        public List<string> AdditionalSourceFolders { get => _additionalSourceFolders; set => SetValue(ref _additionalSourceFolders, value); }

        private string _destinationBase = "";
        public string DestinationBase { get => _destinationBase; set => SetValue(ref _destinationBase, value); }

        private int _pollingIntervalSeconds = 30;
        public int PollingIntervalSeconds { get => _pollingIntervalSeconds; set => SetValue(ref _pollingIntervalSeconds, value); }

        private bool _usePlayniteDetection = true;
        public bool UsePlayniteDetection { get => _usePlayniteDetection; set => SetValue(ref _usePlayniteDetection, value); }

        private bool _useWindowFallback = true;
        public bool UseWindowFallback { get => _useWindowFallback; set => SetValue(ref _useWindowFallback, value); }

        // private bool _autoCreateFolders = false;
        // public bool AutoCreateFolders { get => _autoCreateFolders; set => SetValue(ref _autoCreateFolders, value); }

        private bool _moveUnmatchedToFolder = false;
        public bool MoveUnmatchedToFolder { get => _moveUnmatchedToFolder; set => SetValue(ref _moveUnmatchedToFolder, value); }

        private string _unmatchedFolderName = "_Unmatched";
        public string UnmatchedFolderName { get => _unmatchedFolderName; set => SetValue(ref _unmatchedFolderName, value); }

        private bool _showNotifications = true;
        public bool ShowNotifications { get => _showNotifications; set => SetValue(ref _showNotifications, value); }

        // ✅ 新增：通知时机子选项
        private bool _notifyOnEachScreenshot = true;
        public bool NotifyOnEachScreenshot
        {
            get => _notifyOnEachScreenshot;
            set => SetValue(ref _notifyOnEachScreenshot, value);
        }

        private bool _notifyOnGameEnd = true;
        public bool NotifyOnGameEnd
        {
            get => _notifyOnGameEnd;
            set => SetValue(ref _notifyOnGameEnd, value);
        }

        private string _renamePattern = "{game}_{date}_{time}";
        public string RenamePattern { get => _renamePattern; set => SetValue(ref _renamePattern, value); }

        // 新增
        private string _customSuffix = "";
        public string CustomSuffix
        {
            get => _customSuffix;
            set => SetValue(ref _customSuffix, value);
        }

        private bool _enableBackup = false;
        public bool EnableBackup { get => _enableBackup; set => SetValue(ref _enableBackup, value); }

        private string _backupFolder = "";
        public string BackupFolder { get => _backupFolder; set => SetValue(ref _backupFolder, value); }

        private bool _enableSteamSupport = false;
        public bool EnableSteamSupport { get => _enableSteamSupport; set => SetValue(ref _enableSteamSupport, value); }

        private string _steamPath = "";
        public string SteamPath { get => _steamPath; set => SetValue(ref _steamPath, value); }

        private bool _enableLocalProviderIntegration = false;
        public bool EnableLocalProviderIntegration { get => _enableLocalProviderIntegration; set => SetValue(ref _enableLocalProviderIntegration, value); }

        private bool _enableEmulatorSupport = false;
        public bool EnableEmulatorSupport { get => _enableEmulatorSupport; set => SetValue(ref _enableEmulatorSupport, value); }

        private bool _enableScreenshotsVisualizerRefresh = false;
        public bool EnableScreenshotsVisualizerRefresh { get => _enableScreenshotsVisualizerRefresh; set => SetValue(ref _enableScreenshotsVisualizerRefresh, value); }

        // Listas iniciam vazias — o serializer popula do JSON sem duplicar
        private List<EmulatorProfile> _emulatorProfiles = new List<EmulatorProfile>();
        public List<EmulatorProfile> EmulatorProfiles { get => _emulatorProfiles; set => SetValue(ref _emulatorProfiles, value); }

        private List<string> _imageExtensions = new List<string>();
        public List<string> ImageExtensions { get => _imageExtensions; set => SetValue(ref _imageExtensions, value); }

        private List<string> _videoExtensions = new List<string>();
        public List<string> VideoExtensions { get => _videoExtensions; set => SetValue(ref _videoExtensions, value); }

        private List<string> _windowBlacklist = new List<string>();
        public List<string> WindowBlacklist { get => _windowBlacklist; set => SetValue(ref _windowBlacklist, value); }

        // Prefixos de arquivo (ex: nome do processo capturado pelo ShareX) que sempre
        // pulam dicionário e janela ativa, usando só o jogo ativo do Playnite.
        // Resolve o problema de core-only emulators (RetroArch etc.) onde o prefixo
        // do arquivo é sempre o mesmo independente da ROM rodando.
        private List<string> _emulatorPrefixes = new List<string>();
        public List<string> EmulatorPrefixes { get => _emulatorPrefixes; set => SetValue(ref _emulatorPrefixes, value); }

        // ✅ 新增：黑名单前缀 - 匹配后直接跳过所有处理
        private List<string> _blacklistPrefixes = new List<string>();
        public List<string> BlacklistPrefixes { get => _blacklistPrefixes; set => SetValue(ref _blacklistPrefixes, value); }

        // ─── New: Match similarity threshold ───
        // Files below this score will not be matched (0-100), default 50
        // private int _matchThreshold = 70;
        // public int MatchThreshold { get => _matchThreshold; set => SetValue(ref _matchThreshold, value); }

        // ─── New: Force create folder ───
        private bool _forceCreateFolder = true;
        public bool ForceCreateFolder
        {
            get => _forceCreateFolder;
            set => SetValue(ref _forceCreateFolder, value);
        }

        // ✅ 新增：强制创建文件夹的子选项
        private bool _forceCreateOnGameStart = false;
        public bool ForceCreateOnGameStart
        {
            get => _forceCreateOnGameStart;
            set => SetValue(ref _forceCreateOnGameStart, value);
        }

        private bool _forceCreateOnScreenshot = true;
        public bool ForceCreateOnScreenshot
        {
            get => _forceCreateOnScreenshot;
            set => SetValue(ref _forceCreateOnScreenshot, value);
        }

        // ─── Notification settings ───
        private bool _enableMismatchNotification = true;
        public bool EnableMismatchNotification
        {
            get => _enableMismatchNotification;
            set => SetValue(ref _enableMismatchNotification, value);
        }

        private bool _limitNotifications = true;
        public bool LimitNotifications
        {
            get => _limitNotifications;
            set => SetValue(ref _limitNotifications, value);
        }
    }

    public class GameSnapSettingsViewModel : ObservableObject, ISettings
    {
        private readonly GameSnapPlugin _plugin;
        private GameSnapSettings _editingClone;

        private GameSnapSettings _settings;
        public GameSnapSettings Settings { get => _settings; set => SetValue(ref _settings, value); }

        // ObservableCollection para o ItemsControl da aba Emulators
        private ObservableCollection<EmulatorProfile> _emulatorProfiles = new ObservableCollection<EmulatorProfile>();
        public ObservableCollection<EmulatorProfile> EmulatorProfiles
        {
            get => _emulatorProfiles;
            set => SetValue(ref _emulatorProfiles, value);
        }

        public GameSnapSettingsViewModel(GameSnapPlugin plugin)
        {
            _plugin = plugin;
            var saved = plugin.LoadPluginSettings<GameSnapSettings>();

            if (saved == null)
            {
                Settings = new GameSnapSettings();
            }
            else
            {
                Settings = saved;
            }

            // Preenche defaults para campos que vieram vazios do JSON (ou primeira execucao)
            if (Settings.ImageExtensions.Count == 0)
                Settings.ImageExtensions = new List<string> { ".png", ".jpg", ".jpeg" };
            if (Settings.VideoExtensions.Count == 0)
                Settings.VideoExtensions = new List<string> { ".mp4", ".wmv" };
            if (Settings.WindowBlacklist.Count == 0)
                Settings.WindowBlacklist = new List<string>
                {
                    "explorer", "notepad", "settings", "task manager",
                    "chrome", "edge", "opera", "firefox", "brave",
                    "discord", "steam", "launcher", "update", "setup",
                    "windows", "desktop", "playnite", "visual studio",
                    "code", "powershell", "cmd", "terminal"
                };

            if (Settings.EmulatorPrefixes.Count == 0)
                Settings.EmulatorPrefixes = new List<string>
                {
                    "retroarch", "pcsx2", "dolphin", "rpcs3",
                    "cemu", "ppsspp", "mgba", "duckstation"
                };

            // ✅ 新增：黑名单前缀默认值
            if (Settings.BlacklistPrefixes.Count == 0)
                Settings.BlacklistPrefixes = new List<string>();

            // Emulator profiles: usa salvos ou cria defaults
            if (Settings.EmulatorProfiles.Count == 0)
            {
                Settings.EmulatorProfiles = EmulatorProfile.CreateDefaults();
            }
            else
            {
                // Adiciona built-ins que podem ter sido adicionados em versoes futuras
                var existingNames = new HashSet<string>(Settings.EmulatorProfiles.Select(p => p.Name));
                foreach (var def in EmulatorProfile.CreateDefaults())
                    if (!existingNames.Contains(def.Name))
                        Settings.EmulatorProfiles.Add(def);
            }

            // Sincroniza para a ObservableCollection da UI
            EmulatorProfiles = new ObservableCollection<EmulatorProfile>(Settings.EmulatorProfiles);
        }

        // ISettings
        public void BeginEdit()
        {
            _editingClone = Serialization.GetClone(Settings);
        }

        public void CancelEdit()
        {
            if (_editingClone == null) return;
            Settings = _editingClone;
            EmulatorProfiles = new ObservableCollection<EmulatorProfile>(Settings.EmulatorProfiles);
        }

        public void EndEdit()
        {
            // Sincroniza ObservableCollection de volta para o DTO antes de salvar
            Settings.EmulatorProfiles = EmulatorProfiles.ToList();
            _plugin.SavePluginSettings(Settings);
            _plugin.ApplySettings(Settings);
        }

        public bool VerifySettings(out List<string> errors)
        {
            errors = new List<string>();
            return true;
        }

        // Text bindings
        [DontSerialize]
        public string ImageExtensionsText
        {
            get => string.Join(", ", Settings.ImageExtensions);
            set
            {
                Settings.ImageExtensions = value
                    .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(s => s.Trim().ToLowerInvariant())
                    .Where(s => s.StartsWith("."))
                    .ToList();
                OnPropertyChanged();
            }
        }

        [DontSerialize]
        public string VideoExtensionsText
        {
            get => string.Join(", ", Settings.VideoExtensions);
            set
            {
                Settings.VideoExtensions = value
                    .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(s => s.Trim().ToLowerInvariant())
                    .Where(s => s.StartsWith("."))
                    .ToList();
                OnPropertyChanged();
            }
        }

        [DontSerialize]
        public string EmulatorPrefixesText
        {
            get => string.Join(", ", Settings.EmulatorPrefixes);
            set
            {
                Settings.EmulatorPrefixes = value
                    .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(s => s.Trim().ToLowerInvariant())
                    .Where(s => !string.IsNullOrEmpty(s))
                    .ToList();
                OnPropertyChanged();
            }
        }

        // ✅ 新增：黑名单文本绑定
        [DontSerialize]
        public string BlacklistPrefixesText
        {
            get => string.Join(", ", Settings.BlacklistPrefixes);
            set
            {
                Settings.BlacklistPrefixes = value
                    .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(s => s.Trim().ToLowerInvariant())
                    .Where(s => !string.IsNullOrEmpty(s))
                    .ToList();
                OnPropertyChanged();
            }
        }

        [DontSerialize]
        public string AdditionalSourcesText
        {
            get => string.Join(Environment.NewLine, Settings.AdditionalSourceFolders);
            set
            {
                Settings.AdditionalSourceFolders = value
                    .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(s => s.Trim())
                    .Where(s => !string.IsNullOrEmpty(s))
                    .ToList();
                OnPropertyChanged();
            }
        }

        // [DontSerialize]
        // public System.Windows.Visibility AutoCreateFoldersWarningVisibility
        //     => Settings.AutoCreateFolders
        //        ? System.Windows.Visibility.Collapsed
        //        : System.Windows.Visibility.Visible;

        // Commands
        public RelayCommand<object> BrowseSourceCommand => new RelayCommand<object>((a) =>
        {
            var path = _plugin.PlayniteApi.Dialogs.SelectFolder();
            if (path != null) Settings.SourceFolder = path;
        });

        public RelayCommand<object> BrowseDestinationCommand => new RelayCommand<object>((a) =>
        {
            var path = _plugin.PlayniteApi.Dialogs.SelectFolder();
            if (path != null) Settings.DestinationBase = path;
        });

        public RelayCommand<object> BrowseBackupCommand => new RelayCommand<object>((a) =>
        {
            var path = _plugin.PlayniteApi.Dialogs.SelectFolder();
            if (path != null) Settings.BackupFolder = path;
        });

        public RelayCommand<object> BrowseSteamCommand => new RelayCommand<object>((a) =>
        {
            var path = _plugin.PlayniteApi.Dialogs.SelectFolder();
            if (path != null) Settings.SteamPath = path;
        });

        public RelayCommand<object> OpenDictionaryCommand => new RelayCommand<object>((a) =>
        {
            var path = Path.Combine(_plugin.GetPluginUserDataPath(), "dictionary.txt");
            if (!File.Exists(path)) File.WriteAllText(path, "# Format:\n# [Game Name]\n# alias1\n");
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("notepad.exe", path) { UseShellExecute = true });
        });

        public RelayCommand<object> OpenLogCommand => new RelayCommand<object>((a) =>
        {
            var path = Path.Combine(_plugin.GetPluginUserDataPath(), "gamesnap.log");
            if (File.Exists(path))
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("notepad.exe", path) { UseShellExecute = true });
            else
                _plugin.PlayniteApi.Dialogs.ShowMessage(
                    _plugin.PlayniteApi.Resources.GetString("LOCGameSnap_NoLogFile") ?? "No log file yet.",
                    "GameSnap");
        });

        public RelayCommand<object> AddEmulatorCommand => new RelayCommand<object>((a) =>
        {
            var result = _plugin.PlayniteApi.Dialogs.SelectString("", "Add Emulator", "Emulator name:");
            if (result == null || !result.Result || string.IsNullOrWhiteSpace(result.SelectedString)) return;
            EmulatorProfiles.Add(new EmulatorProfile
            {
                Name        = result.SelectedString.Trim(),
                Enabled     = true,
                IsUserAdded = true
            });
        });

        public RelayCommand<object> RemoveEmulatorCommand => new RelayCommand<object>((a) =>
        {
            for (int i = EmulatorProfiles.Count - 1; i >= 0; i--)
                if (EmulatorProfiles[i].IsUserAdded)
                {
                    EmulatorProfiles.RemoveAt(i);
                    return;
                }
        });

        public string? BrowseForFolder()
            => _plugin.PlayniteApi.Dialogs.SelectFolder();
    }
}
