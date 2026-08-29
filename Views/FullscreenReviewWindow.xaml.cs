using Playnite.SDK;
using Playnite.SDK.Models;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media.Imaging;

namespace GameSnapPlugin.Views
{
    public class UnmatchedFileItem
    {
        public string FullPath   { get; set; }
        public string FileName   { get; set; }
        public string DateString { get; set; }

        public UnmatchedFileItem(string path)
        {
            FullPath   = path;
            FileName   = Path.GetFileName(path);
            DateString = new FileInfo(path).LastWriteTime.ToString("yyyy-MM-dd HH:mm");
        }
    }

    // Representa um destino possível — jogo do Playnite OU pasta existente no destino
    public class DestinationItem
    {
        public string Name       { get; set; }
        public string FolderName { get; set; } // nome sanitizado para uso no disco
        public bool   IsFolder   { get; set; } // true = pasta existente, false = jogo Playnite

        public DestinationItem(string name, string folderName, bool isFolder)
        {
            Name       = name;
            FolderName = folderName;
            IsFolder   = isFolder;
        }
    }

    public partial class FullscreenReviewWindow : Window
    {
        private readonly IPlayniteAPI      _api;
        private readonly GameSnapSettings  _settings;
        private readonly DictionaryService _dict;
        private readonly GameSnapLogger    _logger;

        private ObservableCollection<UnmatchedFileItem> _files = new ObservableCollection<UnmatchedFileItem>();
        private List<DestinationItem>                   _allDestinations = new List<DestinationItem>();
        private ObservableCollection<DestinationItem>   _filteredDestinations = new ObservableCollection<DestinationItem>();

        // Estado da tela de busca
        private string _searchQuery = "";

        public FullscreenReviewWindow(
            IPlayniteAPI api,
            GameSnapSettings settings,
            DictionaryService dict,
            OrganizerService organizer,
            GameSnapLogger logger)
        {
            InitializeComponent();
            this.KeyDown += Window_KeyDown;

            _api      = api;
            _settings = settings;
            _dict     = dict;
            _logger   = logger;

            LoadFiles();
            LoadGames();

            FileList.ItemsSource         = _files;
            SearchResultList.ItemsSource = _filteredDestinations;

            if (_files.Count > 0)
                FileList.SelectedIndex = 0;

            Loaded += (s, e) => FileList.Focus();
        }

        // ── Data ─────────────────────────────────────────────────────────────────

        private void LoadFiles()
        {
            var path = Path.Combine(_settings.DestinationBase, _settings.UnmatchedFolderName);
            _files.Clear();

            if (!Directory.Exists(path)) return;

            var exts = _settings.ImageExtensions
                .Concat(_settings.VideoExtensions)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var f in Directory.GetFiles(path)
                .Where(f => exts.Contains(Path.GetExtension(f)))
                .OrderByDescending(f => new FileInfo(f).LastWriteTime))
                _files.Add(new UnmatchedFileItem(f));

            UpdateMainCounters();
        }

        private void LoadGames()
        {
            var invalid = Path.GetInvalidFileNameChars();

            // Pastas existentes no destino (inclui +Playnite, _Unmatched, etc.)
            var existingFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var destinations    = new List<DestinationItem>();

            if (Directory.Exists(_settings.DestinationBase))
            {
                foreach (var dir in Directory.GetDirectories(_settings.DestinationBase)
                    .OrderBy(d => d))
                {
                    var folderName = Path.GetFileName(dir);
                    // Pula a pasta de não identificados
                    if (folderName == _settings.UnmatchedFolderName) continue;
                    existingFolders.Add(folderName);
                    destinations.Add(new DestinationItem(folderName, folderName, isFolder: true));
                }
            }

            // Jogos do Playnite que ainda não têm pasta criada
            foreach (var game in _api.Database.Games
                .GroupBy(g => g.Name)
                .Select(g => g.First())
                .OrderBy(g => g.Name))
            {
                var folderName = string.Concat(game.Name.Split(invalid)).Trim();
                if (!existingFolders.Contains(folderName))
                    destinations.Add(new DestinationItem(game.Name, folderName, isFolder: false));
            }

            _allDestinations = destinations.OrderBy(d => d.Name).ToList();
        }

        private void UpdateMainCounters()
        {
            CounterLabel.Text  = $"{_files.Count} file(s) pending";
            SubtitleLabel.Text = "";
        }

        // ── Main screen ──────────────────────────────────────────────────────────

        private void FileList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (FileList.SelectedItem is not UnmatchedFileItem item) return;

            SubtitleLabel.Text = item.FileName;
            LoadPreview(item.FullPath);
        }

        private void LoadPreview(string path)
        {
            try
            {
                var ext = Path.GetExtension(path).ToLowerInvariant();
                if (_settings.ImageExtensions.Contains(ext))
                {
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.UriSource   = new Uri(path);
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.EndInit();
                    PreviewImage.Source       = bmp;
                    PreviewImage.Visibility   = Visibility.Visible;
                    NoPreviewLabel.Visibility = Visibility.Collapsed;
                }
                else
                {
                    PreviewImage.Source       = null;
                    PreviewImage.Visibility   = Visibility.Collapsed;
                    NoPreviewLabel.Text = _api.Resources.GetString("LOCGameSnapFullscreen_VideoNoPreview") ?? "Video — no preview";
                    NoPreviewLabel.Visibility = Visibility.Visible;
                }
            }
            catch
            {
                PreviewImage.Source       = null;
                PreviewImage.Visibility   = Visibility.Collapsed;
                NoPreviewLabel.Text = _api.Resources.GetString("LOCGameSnapFullscreen_CannotLoadPreview") ?? "Cannot load preview";
                NoPreviewLabel.Visibility = Visibility.Visible;
            }
        }

        // ── Search screen ────────────────────────────────────────────────────────

        private void OpenSearchScreen()
        {
            _searchQuery = "";
            UpdateSearchQuery();
            FilterGames();

            var current = FileList.SelectedItem as UnmatchedFileItem;
            SearchFileLabel.Text = current?.FileName ?? "";

            MainScreen.Visibility   = Visibility.Collapsed;
            SearchScreen.Visibility = Visibility.Visible;

            // Foca a primeira tecla do teclado
            FocusFirstKey();
        }

        private void CloseSearchScreen()
        {
            SearchScreen.Visibility = Visibility.Collapsed;
            MainScreen.Visibility   = Visibility.Visible;
            FileList.Focus();
        }

        private void UpdateSearchQuery()
        {
            SearchQueryLabel.Text = _searchQuery;
            // O cursor fica depois do texto
            SearchCursorLabel.Margin = new Thickness(
                Math.Max(0, _searchQuery.Length * 16.8), 0, 0, 0);
        }

        private void FilterGames()
        {
            _filteredDestinations.Clear();

            var matches = string.IsNullOrEmpty(_searchQuery)
                ? _allDestinations
                : _allDestinations.Where(d => d.Name.IndexOf(_searchQuery, StringComparison.OrdinalIgnoreCase) >= 0);

            foreach (var d in matches)
                _filteredDestinations.Add(d);

            if (_filteredDestinations.Count > 0)
                SearchResultList.SelectedIndex = 0;
        }

        // ── Keyboard logic ───────────────────────────────────────────────────────

        private void Key_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn) return;
            var tag = btn.Tag as string ?? "";
            ProcessKey(tag);
        }

        private void ProcessKey(string key)
        {
            switch (key)
            {
                case "BACK":
                    if (_searchQuery.Length > 0)
                        _searchQuery = _searchQuery.Substring(0, _searchQuery.Length - 1);
                    break;
                case "SPACE":
                    _searchQuery += " ";
                    break;
                case "CLEAR":
                    _searchQuery = "";
                    break;
                default:
                    _searchQuery += key;
                    break;
            }

            UpdateSearchQuery();
            FilterGames();
        }

        private void FocusFirstKey()
        {
            // Foca o primeiro botão do teclado virtual
            var rows = new[] { 0, 1, 2, 3 };
            foreach (var row in VirtualKeyboard.Children.OfType<UniformGrid>())
            {
                var first = row.Children.OfType<Button>().FirstOrDefault();
                if (first != null) { first.Focus(); return; }
            }
        }

        // ── Actions ──────────────────────────────────────────────────────────────

        private static string SanitizeFileName(string name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            return string.Concat(name.Split(invalid)).Trim();
        }

        private string BuildDestName(string gameName, string originalPath)
        {
            var ext      = Path.GetExtension(originalPath);
            var original = Path.GetFileNameWithoutExtension(originalPath);
            var info     = new FileInfo(originalPath);
            var date     = info.LastWriteTime;

            var pattern = string.IsNullOrWhiteSpace(_settings.RenamePattern)
                ? "{game}_{date}_{time}"
                : _settings.RenamePattern;

            return pattern
                .Replace("{game}",     SanitizeFileName(gameName))
                .Replace("{date}",     date.ToString("yyyy-MM-dd"))
                .Replace("{time}",     date.ToString("HH_mm_ss"))
                .Replace("{datetime}", date.ToString("yyyy-MM-dd_HH_mm_ss"))
                .Replace("{original}", SanitizeFileName(original))
                + ext;
        }

        private void AssignCurrentFile()
        {
            if (FileList.SelectedItem is not UnmatchedFileItem file) return;
            if (SearchResultList.SelectedItem is not DestinationItem dest) return;

            try
            {
                var destFolder = Path.Combine(_settings.DestinationBase, dest.FolderName);
                Directory.CreateDirectory(destFolder);

                // Aplica o RenamePattern (igual ao OrganizerService)
                var newFileName = BuildDestName(dest.Name, file.FullPath);
                var destPath    = Path.Combine(destFolder, newFileName);

                // Evita sobrescrever arquivo existente
                if (File.Exists(destPath))
                {
                    var stem = Path.GetFileNameWithoutExtension(newFileName);
                    var ext  = Path.GetExtension(newFileName);
                    destPath = Path.Combine(destFolder, $"{stem}_{Guid.NewGuid().ToString("N").Substring(0, 6)}{ext}");
                }

                File.Move(file.FullPath, destPath);

                var prefix = Path.GetFileNameWithoutExtension(file.FileName).Split('_')[0];
                if (!string.IsNullOrWhiteSpace(prefix))
                    _dict.SaveAlias(prefix, dest.Name);

                _logger.Info($"[Fullscreen Review] {file.FileName} -> {dest.FolderName}/{newFileName}");

                _files.Remove(file);
                UpdateMainCounters();
                CloseSearchScreen();

                if (_files.Count > 0)
                    FileList.SelectedIndex = 0;
                else
                    Close();
            }
            catch (Exception ex)
            {
                _logger.Error($"[Fullscreen Review] Assign failed: {ex.Message} | File: {file.FullPath} | Dest: {dest?.Name}");
                _api.Dialogs.ShowMessage(
                    string.Format(
                        _api.Resources.GetString("LOCGameSnapFullscreen_AssignFailed") ?? "Failed to assign: {0}",
                        ex.Message),
                    "GameSnap");
                CloseSearchScreen();
            }
        }

        private void SkipFile()
        {
            if (_files.Count == 0) return;
            var idx = FileList.SelectedIndex;
            FileList.SelectedIndex = (idx + 1) % _files.Count;
        }

        private void DeleteCurrentFile()
        {
            if (FileList.SelectedItem is not UnmatchedFileItem file) return;

            var result = _api.Dialogs.ShowMessage(
                string.Format(
                    _api.Resources.GetString("LOCGameSnapFullscreen_DeleteConfirm") ?? "Delete {0}?",
                    file.FileName),
                "GameSnap",
                MessageBoxButton.YesNo);

            if (result != MessageBoxResult.Yes) return;

            try
            {
                File.Delete(file.FullPath);
                _files.Remove(file);
                UpdateMainCounters();
                if (_files.Count > 0)
                    FileList.SelectedIndex = 0;
                else
                    Close();
            }
            catch (Exception ex)
            {
                _logger.Error($"[Fullscreen Review] Delete failed: {ex.Message}");
            }
        }

        // ── Keyboard / gamepad input ─────────────────────────────────────────────
        //
        // TELA PRINCIPAL:
        //   D-pad ↑↓  → navega lista de arquivos (WPF nativo)
        //   A (Enter) → abre tela de busca
        //   B (Esc)   → fecha janela
        //   Start(F1) → skip arquivo
        //
        // TELA DE BUSCA:
        //   D-pad ↑↓←→ → navega teclas do teclado virtual (WPF nativo via Tab/Arrow)
        //   A (Enter)  → se no teclado: digita tecla; se na lista: confirma assign
        //   B (Esc)    → volta para tela principal
        //   D-pad ↓ (na última linha do teclado) → entra na lista de resultados
        //   D-pad ↑ (na lista) → volta para o teclado

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            bool isSearchOpen = SearchScreen.Visibility == Visibility.Visible;
            bool keyboardFocused = VirtualKeyboard.IsKeyboardFocusWithin;
            bool resultsFocused  = SearchResultList.IsKeyboardFocusWithin;

            if (!isSearchOpen)
            {
                // Tela principal
                switch (e.Key)
                {
                    case Key.Enter:
                        OpenSearchScreen();
                        e.Handled = true;
                        break;
                    case Key.Escape:
                        Close();
                        e.Handled = true;
                        break;
                    case Key.F1:
                        SkipFile();
                        e.Handled = true;
                        break;
                    case Key.Delete:
                        DeleteCurrentFile();
                        e.Handled = true;
                        break;
                }
            }
            else
            {
                // Tela de busca
                switch (e.Key)
                {
                    case Key.Escape:
                        CloseSearchScreen();
                        e.Handled = true;
                        break;

                    case Key.Enter:
                        if (resultsFocused)
                            AssignCurrentFile();
                        else if (keyboardFocused)
                        {
                            // Enter no teclado virtual já é tratado pelo Button.Click
                            // mas como estamos interceptando, precisamos clicar manualmente
                            var focused = Keyboard.FocusedElement as Button;
                            if (focused != null)
                                ProcessKey(focused.Tag as string ?? "");
                            e.Handled = true;
                        }
                        break;

                    case Key.Down when resultsFocused:
                        // Já navega pela lista nativo — não interceptar
                        break;

                    case Key.Up when resultsFocused:
                        if (SearchResultList.SelectedIndex <= 0)
                        {
                            // Sobe de volta para o teclado
                            FocusFirstKey();
                            e.Handled = true;
                        }
                        break;

                    case Key.Down when keyboardFocused:
                        // Verifica se estamos na última linha do teclado
                        var rows = VirtualKeyboard.Children.OfType<UniformGrid>().ToList();
                        var lastRow = rows.LastOrDefault();
                        if (lastRow != null && lastRow.IsKeyboardFocusWithin)
                        {
                            // Desce para a lista de resultados
                            SearchResultList.Focus();
                            if (SearchResultList.Items.Count > 0)
                                SearchResultList.SelectedIndex = 0;
                            e.Handled = true;
                        }
                        break;
                }
            }
        }
    }
}
