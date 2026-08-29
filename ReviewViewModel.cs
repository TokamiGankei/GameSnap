using Playnite.SDK;
using Playnite.SDK.Models;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;

namespace GameSnapPlugin
{
    public class UnmatchedFileItem
    {
        public string FilePath { get; set; } = "";
        public string FileName { get; set; } = "";
        public string DateText { get; set; } = "";
    }

    public class ReviewViewModel : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        private readonly IPlayniteAPI      _playniteApi;
        private readonly GameSnapSettings  _settings;
        private readonly DictionaryService _dictionary;
        private readonly OrganizerService  _organizer;
        private readonly GameSnapLogger    _logger;

        // ── Collections ──
        public ObservableCollection<UnmatchedFileItem> UnmatchedFiles { get; } = new();

        private List<Game> _allGames = new();

        private ObservableCollection<Game> _filteredGames = new();
        public ObservableCollection<Game> FilteredGames
        {
            get => _filteredGames;
            set { _filteredGames = value; OnPropertyChanged(); }
        }

        // ── Selected file ──
        private UnmatchedFileItem? _selectedFile;
        public UnmatchedFileItem? SelectedFile
        {
            get => _selectedFile;
            set
            {
                _selectedFile = value;
                OnPropertyChanged();
                LoadPreview(value?.FilePath);
                OnPropertyChanged(nameof(PreviewInfoVisibility));
                OnPropertyChanged(nameof(NoPreviewVisibility));
                OnPropertyChanged(nameof(PreviewInfo));
                OnPropertyChanged(nameof(StatusText));
            }
        }

        // ── Selected game ──
        private Game? _selectedGame;
        public Game? SelectedGame
        {
            get => _selectedGame;
            set { _selectedGame = value; OnPropertyChanged(); }
        }

        // ── Preview ──
        private BitmapImage? _previewImage;
        public BitmapImage? PreviewImage
        {
            get => _previewImage;
            set { _previewImage = value; OnPropertyChanged(); }
        }

        public Visibility NoPreviewVisibility  => PreviewImage == null ? Visibility.Visible : Visibility.Collapsed;
        public Visibility PreviewInfoVisibility => PreviewImage != null ? Visibility.Visible : Visibility.Collapsed;

        public string PreviewInfo => _selectedFile != null
            ? $"{_selectedFile.FileName}  •  {_selectedFile.DateText}"
            : "";

        // ── Filter ──
        private string _gameFilter = "";
        public string GameFilter
        {
            get => _gameFilter;
            set
            {
                _gameFilter = value;
                OnPropertyChanged();
                ApplyFilter();
                OnPropertyChanged(nameof(PlaceholderVisibility));
            }
        }

        public Visibility PlaceholderVisibility =>
            string.IsNullOrEmpty(_gameFilter) ? Visibility.Visible : Visibility.Collapsed;

        // ── Status ──
        public string StatusText =>
            $"{UnmatchedFiles.Count} file(s) pending" +
            (_selectedFile != null ? $"  •  {_selectedFile.FileName}" : "");

        // ── Commands ──
        public ICommand AssignCommand { get; }
        public ICommand DeleteCommand { get; }
        public ICommand SkipCommand   { get; }
        public ICommand CloseCommand  { get; }

        private Action? _closeAction;

        public ReviewViewModel(
            IPlayniteAPI      playniteApi,
            GameSnapSettings  settings,
            DictionaryService dictionary,
            OrganizerService  organizer,
            GameSnapLogger    logger)
        {
            _playniteApi = playniteApi;
            _settings    = settings;
            _dictionary  = dictionary;
            _organizer   = organizer;
            _logger      = logger;

            AssignCommand = new RelayCommand(Assign);
            DeleteCommand = new RelayCommand(Delete);
            SkipCommand   = new RelayCommand(Skip);
            CloseCommand  = new RelayCommand(() => _closeAction?.Invoke());

            LoadUnmatchedFiles();
            LoadGames();
        }

        public void SetCloseAction(Action action) => _closeAction = action;

        // ──────────────────────────────────────────────
        // Load
        // ──────────────────────────────────────────────
        private void LoadUnmatchedFiles()
        {
            UnmatchedFiles.Clear();

            if (string.IsNullOrEmpty(_settings.DestinationBase)) return;

            var unmatchedDir = Path.Combine(
                _settings.DestinationBase,
                _settings.UnmatchedFolderName);

            if (!Directory.Exists(unmatchedDir)) return;

            var allExts = _settings.ImageExtensions
                .Concat(_settings.VideoExtensions)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var file in Directory.GetFiles(unmatchedDir)
                .Where(f => allExts.Contains(Path.GetExtension(f).ToLowerInvariant()))
                .OrderByDescending(f => new FileInfo(f).LastWriteTime))
            {
                var info = new FileInfo(file);
                UnmatchedFiles.Add(new UnmatchedFileItem
                {
                    FilePath = file,
                    FileName = info.Name,
                    DateText = info.LastWriteTime.ToString("yyyy-MM-dd HH:mm")
                });
            }

            OnPropertyChanged(nameof(StatusText));
        }

        private void LoadGames()
        {
            _allGames = _playniteApi.Database.Games
                .OrderBy(g => g.Name)
                .ToList();
            ApplyFilter();
        }

        private void ApplyFilter()
        {
            var filter = _gameFilter.Trim();
            var filtered = string.IsNullOrEmpty(filter)
                ? _allGames
                : _allGames.Where(g =>
                    g.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0).ToList();

            FilteredGames = new ObservableCollection<Game>(filtered);
        }

        // ──────────────────────────────────────────────
        // Preview
        // ──────────────────────────────────────────────
        private void LoadPreview(string? filePath)
        {
            if (filePath == null || !File.Exists(filePath))
            {
                PreviewImage = null;
                return;
            }

            var ext = Path.GetExtension(filePath).ToLowerInvariant();
            if (!_settings.ImageExtensions.Contains(ext))
            {
                PreviewImage = null;
                return;
            }

            try
            {
                var img = new BitmapImage();
                img.BeginInit();
                img.CacheOption    = BitmapCacheOption.OnLoad;
                img.UriSource      = new Uri(filePath);
                img.DecodePixelWidth = 900; // limita RAM
                img.EndInit();
                img.Freeze();
                PreviewImage = img;
            }
            catch
            {
                PreviewImage = null;
            }
        }

        // ──────────────────────────────────────────────
        // Actions
        // ──────────────────────────────────────────────
        private void Assign()
        {
            if (_selectedFile == null || _selectedGame == null)
            {
                _playniteApi.Dialogs.ShowMessage(
                    _playniteApi.Resources.GetString("LOCGameSnap_Review_SelectFileAndGame") ?? "Select a file and a game first.",
                    "GameSnap");
                return;
            }

            var gameName = _selectedGame.Name;
            var ext      = Path.GetExtension(_selectedFile.FilePath).ToLowerInvariant();

            // Encontra a pasta do jogo no destino
            var normGame = DictionaryService.Normalize(gameName);
            string? destDir = null;

            if (Directory.Exists(_settings.DestinationBase))
            {
                foreach (var dir in Directory.GetDirectories(_settings.DestinationBase))
                {
                    var normFolder = DictionaryService.Normalize(Path.GetFileName(dir));
                    if (normFolder.Contains(normGame) || normGame.Contains(normFolder))
                    {
                        destDir = dir;
                        break;
                    }
                }
            }

            // Cria a pasta se ForceCreateFolder estiver ativo
            if (destDir == null && _settings.ForceCreateFolder)
            {
                var invalid    = Path.GetInvalidFileNameChars();
                var folderName = string.Concat(gameName.Split(invalid)).Trim();
                destDir = Path.Combine(_settings.DestinationBase, folderName);
                Directory.CreateDirectory(destDir);
            }

            if (destDir == null)
            {
                _playniteApi.Dialogs.ShowMessage(
                    string.Format(
                        _playniteApi.Resources.GetString("LOCGameSnap_Review_NoFolderFound") ?? "No folder found for '{0}'. Please create the folder manually.",
                        gameName),
                    "GameSnap");
                return;
            }

            // Move o arquivo
            var date     = new FileInfo(_selectedFile.FilePath).LastWriteTime;
            var destName = $"{gameName}_{date:yyyy-MM-dd_HH_mm_ss}{ext}";
            var destPath = Path.Combine(destDir, destName);

            int i = 1;
            while (File.Exists(destPath))
            {
                destPath = Path.Combine(destDir, $"{gameName}_{date:yyyy-MM-dd_HH_mm_ss}_{i}{ext}");
                i++;
            }

            try
            {
                PreviewImage = null; // libera o lock do arquivo
                File.Move(_selectedFile.FilePath, destPath);

                // Aprende o alias
                var prefix = Path.GetFileNameWithoutExtension(_selectedFile.FileName)
                    .Split('_')[0];
                _dictionary.SaveAlias(prefix, gameName);

                _logger.Write(LogType.Move,
                    $"Review: {_selectedFile.FileName} → {gameName} (manual)");

                RemoveCurrent();
            }
            catch (Exception ex)
            {
                _playniteApi.Dialogs.ShowMessage(
                    string.Format(
                        _playniteApi.Resources.GetString("LOCGameSnap_Review_MoveFailed") ?? "Failed to move file: {0}",
                        ex.Message),
                    "GameSnap");
            }
        }

        private void Delete()
        {
            if (_selectedFile == null) return;

            var confirm = _playniteApi.Dialogs.ShowMessage(
                string.Format(
                    _playniteApi.Resources.GetString("LOCGameSnap_Review_DeleteConfirm") ?? "Delete '{0}'? This cannot be undone.",
                    _selectedFile.FileName),
                "GameSnap",
                MessageBoxButton.YesNo);

            if (confirm != MessageBoxResult.Yes) return;

            try
            {
                PreviewImage = null;
                File.Delete(_selectedFile.FilePath);
                _logger.Write(LogType.Info, $"Review: deleted {_selectedFile.FileName}");
                RemoveCurrent();
            }
            catch (Exception ex)
            {
                _playniteApi.Dialogs.ShowMessage(
                    string.Format(
                        _playniteApi.Resources.GetString("LOCGameSnap_Review_DeleteFailed") ?? "Failed to delete file: {0}",
                        ex.Message),
                    "GameSnap");
            }
        }

        private void Skip()
        {
            if (_selectedFile == null || UnmatchedFiles.Count == 0) return;

            var idx = UnmatchedFiles.IndexOf(_selectedFile);
            var next = idx + 1 < UnmatchedFiles.Count ? idx + 1 : 0;
            SelectedFile = UnmatchedFiles.Count > 1 ? UnmatchedFiles[next] : null;
        }

        private void RemoveCurrent()
        {
            if (_selectedFile == null) return;

            var idx = UnmatchedFiles.IndexOf(_selectedFile);
            UnmatchedFiles.Remove(_selectedFile);

            if (UnmatchedFiles.Count == 0)
            {
                SelectedFile = null;
                _playniteApi.Dialogs.ShowMessage(
                    _playniteApi.Resources.GetString("LOCGameSnap_Review_AllReviewed") ?? "All files reviewed!",
                    "GameSnap");
                _closeAction?.Invoke();
                return;
            }

            SelectedFile = UnmatchedFiles[Math.Min(idx, UnmatchedFiles.Count - 1)];
            OnPropertyChanged(nameof(StatusText));
        }
    }
}
