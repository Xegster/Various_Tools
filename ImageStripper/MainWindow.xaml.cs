using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace ImageStripper
{
    public partial class MainWindow : Window
    {
        private const string BackupFolderName = "Backup";

        private static readonly string[] SupportedExtensions =
        {
            ".jpg", ".jpeg", ".jpe", ".jfif", ".png", ".tif", ".tiff",
            ".gif", ".bmp", ".dib", ".wdp", ".jxr", ".ico", ".dng"
        };

        private readonly ObservableCollection<ImageFileRow> _files = new();
        private readonly ObservableCollection<TagRow> _tagRows = new();
        private readonly HashSet<string> _selectedQueries = new(StringComparer.Ordinal);

        private CancellationTokenSource? _cancellationTokenSource;
        private bool _isProcessing;

        public MainWindow()
        {
            InitializeComponent();

            FileListBox.ItemsSource = _files;
            TagsDataGrid.ItemsSource = _tagRows;

            Log("Ready. Select image files or a folder to begin.");
        }

        // ---- Selecting files ---------------------------------------------------

        private async void BrowseFilesButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Title = "Select one or more images",
                Multiselect = true,
                Filter = "Images (*.jpg;*.jpeg;*.png;*.tif;*.tiff;*.gif;*.bmp)" +
                         "|*.jpg;*.jpeg;*.jpe;*.jfif;*.png;*.tif;*.tiff;*.gif;*.bmp;*.dng" +
                         "|All Files (*.*)|*.*"
            };

            if (dialog.ShowDialog() != true) return;

            string[] selected = dialog.FileNames;
            SourcePathTextBox.Text = selected.Length == 1
                ? selected[0]
                : $"{selected.Length} file(s) selected";

            await LoadAsync(selected);
        }

        private async void BrowseFolderButton_Click(object sender, RoutedEventArgs e)
        {
            using var folderDialog = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "Select a folder containing images",
                ShowNewFolderButton = false
            };

            if (folderDialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

            SourcePathTextBox.Text = folderDialog.SelectedPath;
            await LoadAsync(new[] { folderDialog.SelectedPath });
        }

        private void Window_DragOver(object sender, DragEventArgs e)
        {
            bool hasFiles = e.Data.GetDataPresent(DataFormats.FileDrop);
            e.Effects = hasFiles && !_isProcessing ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private async void Window_Drop(object sender, DragEventArgs e)
        {
            if (_isProcessing) return;
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;

            if (e.Data.GetData(DataFormats.FileDrop) is not string[] dropped || dropped.Length == 0) return;

            SourcePathTextBox.Text = dropped.Length == 1 ? dropped[0] : $"{dropped.Length} item(s) dropped";
            await LoadAsync(dropped);
        }

        private void ClearListButton_Click(object sender, RoutedEventArgs e)
        {
            _files.Clear();
            _tagRows.Clear();
            _selectedQueries.Clear();
            SourcePathTextBox.Text = "";
            UpdateSummaries();
            Log("Cleared the image list.");
        }

        /// <summary>Expands the dropped/selected paths into image files and scans them.</summary>
        private async Task LoadAsync(IEnumerable<string> paths)
        {
            bool recurse = RecurseSubfoldersCheckBox.IsChecked == true;
            List<string> imagePaths;

            try
            {
                imagePaths = CollectImageFiles(paths, recurse);
            }
            catch (Exception ex)
            {
                Log($"Error scanning for images: {ex.Message}");
                MessageBox.Show($"Could not list the images: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            if (imagePaths.Count == 0)
            {
                Log("No supported image files were found.");
                MessageBox.Show("No supported image files were found.", "Information",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            _files.Clear();
            _tagRows.Clear();
            _selectedQueries.Clear();

            foreach (string path in imagePaths)
            {
                _files.Add(new ImageFileRow(path, OnFileInclusionChanged));
            }

            Log($"Found {imagePaths.Count} image file(s). Reading metadata...");
            await ScanAsync();
        }

        private List<string> CollectImageFiles(IEnumerable<string> paths, bool recurse)
        {
            var found = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (string path in paths)
            {
                if (Directory.Exists(path))
                {
                    SearchOption option = recurse ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;

                    foreach (string file in Directory.EnumerateFiles(path, "*", option))
                    {
                        if (!IsSupported(file)) continue;
                        if (IsInBackupFolder(file)) continue;
                        if (seen.Add(file)) found.Add(file);
                    }
                }
                else if (File.Exists(path))
                {
                    // An explicitly chosen file is honoured even if the extension is unusual.
                    if (seen.Add(path)) found.Add(path);
                }
            }

            found.Sort(StringComparer.OrdinalIgnoreCase);
            return found;
        }

        private static bool IsSupported(string path)
        {
            string extension = Path.GetExtension(path);
            return SupportedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);
        }

        private static bool IsInBackupFolder(string path)
        {
            string? directory = Path.GetDirectoryName(path);
            if (directory == null) return false;

            string[] parts = directory.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return parts.Any(part => part.Equals(BackupFolderName, StringComparison.OrdinalIgnoreCase));
        }

        // ---- Scanning ----------------------------------------------------------

        private async Task ScanAsync()
        {
            var targets = _files.ToList();
            if (targets.Count == 0) return;

            try
            {
                SetProcessingState(true, targets.Count);
                _cancellationTokenSource = new CancellationTokenSource();
                CancellationToken token = _cancellationTokenSource.Token;

                int withMetadata = 0;
                int failed = 0;

                for (int i = 0; i < targets.Count; i++)
                {
                    token.ThrowIfCancellationRequested();

                    ImageFileRow row = targets[i];
                    string path = row.FullPath;
                    ImageScanResult scan = await Task.Run(() => MetadataReader.Read(path), token);

                    row.ApplyScan(scan);

                    if (scan.Error != null)
                    {
                        failed++;
                        Log($"{row.FileName}: could not read metadata - {scan.Error}");
                    }
                    else if (scan.Tags.Count > 0)
                    {
                        withMetadata++;
                    }

                    ProgressBar.Value = i + 1;
                }

                Log($"Scan complete. {withMetadata} file(s) contain metadata, {failed} file(s) could not be read.");
            }
            catch (OperationCanceledException)
            {
                Log("Scan cancelled by user.");
            }
            catch (Exception ex)
            {
                Log($"Error: {ex.Message}");
                MessageBox.Show($"An error occurred while reading metadata: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                SetProcessingState(false, 0);
                _cancellationTokenSource?.Dispose();
                _cancellationTokenSource = null;
            }

            if (FileListBox.SelectedItem == null)
            {
                ImageFileRow? first = _files.FirstOrDefault(f => !f.HasError && f.Tags.Count > 0) ?? _files.FirstOrDefault();
                FileListBox.SelectedItem = first;
            }

            RebuildTagRows();
        }

        // ---- Tag list ----------------------------------------------------------

        private void FileListBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => RebuildTagRows();

        private void ShowAllTagsCheckBox_Changed(object sender, RoutedEventArgs e) => RebuildTagRows();

        private void FilterTextBox_TextChanged(object sender, TextChangedEventArgs e) => RebuildTagRows();

        private void OnFileInclusionChanged() => RebuildTagRows();

        private void RebuildTagRows()
        {
            // The grid is rebuilt from scratch; the checked state lives in _selectedQueries.
            _tagRows.Clear();

            List<ImageFileRow> included = _files.Where(f => f.IsIncluded && !f.HasError).ToList();

            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (ImageFileRow file in included)
            {
                foreach (MetadataTag tag in file.Tags)
                {
                    counts.TryGetValue(tag.Query, out int current);
                    counts[tag.Query] = current + 1;
                }
            }

            IEnumerable<ImageFileRow> sources;
            if (ShowAllTagsCheckBox.IsChecked == true)
            {
                sources = included;
            }
            else if (FileListBox.SelectedItem is ImageFileRow selected)
            {
                sources = new[] { selected };
            }
            else
            {
                sources = Enumerable.Empty<ImageFileRow>();
            }

            string filter = FilterTextBox.Text.Trim();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var rows = new List<TagRow>();

            foreach (ImageFileRow file in sources)
            {
                foreach (MetadataTag tag in file.Tags)
                {
                    if (!seen.Add(tag.Query)) continue;
                    if (!MatchesFilter(tag, filter)) continue;

                    counts.TryGetValue(tag.Query, out int fileCount);
                    rows.Add(new TagRow(tag, fileCount, _selectedQueries, UpdateSummaries));
                }
            }

            rows.Sort((left, right) =>
            {
                int byGroup = string.Compare(left.Group, right.Group, StringComparison.OrdinalIgnoreCase);
                if (byGroup != 0) return byGroup;

                int byName = string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase);
                return byName != 0 ? byName : string.Compare(left.Query, right.Query, StringComparison.Ordinal);
            });

            foreach (TagRow row in rows) _tagRows.Add(row);

            UpdateSummaries();
        }

        private static bool MatchesFilter(MetadataTag tag, string filter)
        {
            if (filter.Length == 0) return true;

            return tag.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
                   || tag.Group.Contains(filter, StringComparison.OrdinalIgnoreCase)
                   || tag.Query.Contains(filter, StringComparison.OrdinalIgnoreCase)
                   || tag.Value.Contains(filter, StringComparison.OrdinalIgnoreCase);
        }

        private void SelectAllTagsButton_Click(object sender, RoutedEventArgs e)
        {
            foreach (TagRow row in _tagRows)
            {
                _selectedQueries.Add(row.Query);
                row.RefreshSelection();
            }

            UpdateSummaries();
        }

        private void SelectNoTagsButton_Click(object sender, RoutedEventArgs e)
        {
            // Clears hidden selections too, so the count always matches what gets removed.
            _selectedQueries.Clear();
            foreach (TagRow row in _tagRows) row.RefreshSelection();
            UpdateSummaries();
        }

        private void InvertTagsButton_Click(object sender, RoutedEventArgs e)
        {
            foreach (TagRow row in _tagRows)
            {
                if (!_selectedQueries.Remove(row.Query)) _selectedQueries.Add(row.Query);
                row.RefreshSelection();
            }

            UpdateSummaries();
        }

        private void SelectGpsButton_Click(object sender, RoutedEventArgs e)
        {
            int matched = 0;

            foreach (TagRow row in _tagRows)
            {
                bool isGps = row.Group.Contains("GPS", StringComparison.OrdinalIgnoreCase)
                             || row.Name.Contains("GPS", StringComparison.OrdinalIgnoreCase);
                if (!isGps) continue;

                _selectedQueries.Add(row.Query);
                row.RefreshSelection();
                matched++;
            }

            UpdateSummaries();
            Log($"Selected {matched} GPS-related tag(s).");
        }

        // ---- Removing ----------------------------------------------------------

        private async void RemoveTagsButton_Click(object sender, RoutedEventArgs e)
        {
            List<ImageFileRow> targets = _files.Where(f => f.IsIncluded && !f.HasError).ToList();
            List<string> queries = _selectedQueries.ToList();

            if (queries.Count == 0)
            {
                MessageBox.Show("Select at least one tag to remove.", "Nothing selected",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (targets.Count == 0)
            {
                MessageBox.Show("Check at least one image to process.", "Nothing selected",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            bool createBackup = CreateBackupCheckBox.IsChecked == true;
            bool preserveDates = PreserveDatesCheckBox.IsChecked == true;

            MessageBoxResult confirm = MessageBox.Show(
                $"Remove {queries.Count} selected tag(s) from {targets.Count} image(s)?\n\n" +
                (createBackup
                    ? "Originals will be copied into a Backup subfolder first."
                    : "No backups will be made - the files are modified in place."),
                "Confirm removal",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (confirm != MessageBoxResult.Yes) return;

            int changed = 0;
            int unchanged = 0;
            int errors = 0;
            int totalRemoved = 0;

            try
            {
                SetProcessingState(true, targets.Count);
                _cancellationTokenSource = new CancellationTokenSource();
                CancellationToken token = _cancellationTokenSource.Token;

                for (int i = 0; i < targets.Count; i++)
                {
                    token.ThrowIfCancellationRequested();

                    ImageFileRow row = targets[i];
                    string path = row.FullPath;

                    RemovalResult result = await Task.Run(
                        () => MetadataRemover.Remove(path, queries, createBackup, preserveDates), token);

                    if (!result.Success)
                    {
                        errors++;
                        Log($"{row.FileName}: FAILED - {result.Message}");
                    }
                    else if (result.Changed)
                    {
                        changed++;
                        totalRemoved += result.RemovedCount;

                        string note = result.Method == "re-encoded"
                            ? " (re-encoded; JPEG pixel data was recompressed)"
                            : "";
                        Log($"{row.FileName}: removed {result.RemovedCount} tag(s) {result.Method}{note}");
                    }
                    else
                    {
                        unchanged++;
                        Log($"{row.FileName}: none of the selected tags were present.");
                    }

                    ProgressBar.Value = i + 1;
                }

                Log($"Removal complete. Changed: {changed}, unchanged: {unchanged}, errors: {errors}, tags removed: {totalRemoved}");
            }
            catch (OperationCanceledException)
            {
                Log("Removal cancelled by user.");
            }
            catch (Exception ex)
            {
                Log($"Error: {ex.Message}");
                MessageBox.Show($"An error occurred: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                SetProcessingState(false, 0);
                _cancellationTokenSource?.Dispose();
                _cancellationTokenSource = null;
            }

            MessageBox.Show(
                $"Removal complete.\n\nFiles changed: {changed}\nFiles unchanged: {unchanged}\nErrors: {errors}\nTags removed: {totalRemoved}",
                "Complete",
                MessageBoxButton.OK,
                MessageBoxImage.Information);

            // Re-read the files so the grid shows what is actually left on disk.
            _selectedQueries.Clear();
            Log("Re-reading metadata...");
            await ScanAsync();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            _cancellationTokenSource?.Cancel();
            Log("Cancellation requested...");
        }

        // ---- UI plumbing -------------------------------------------------------

        private void SetProcessingState(bool isProcessing, int itemCount)
        {
            _isProcessing = isProcessing;

            BrowseFilesButton.IsEnabled = !isProcessing;
            BrowseFolderButton.IsEnabled = !isProcessing;
            ClearListButton.IsEnabled = !isProcessing;
            RecurseSubfoldersCheckBox.IsEnabled = !isProcessing;
            FileListBox.IsEnabled = !isProcessing;
            TagsDataGrid.IsEnabled = !isProcessing;
            CheckAllFilesButton.IsEnabled = !isProcessing;
            UncheckAllFilesButton.IsEnabled = !isProcessing;
            SelectAllTagsButton.IsEnabled = !isProcessing;
            SelectNoTagsButton.IsEnabled = !isProcessing;
            InvertTagsButton.IsEnabled = !isProcessing;
            SelectGpsButton.IsEnabled = !isProcessing;
            CreateBackupCheckBox.IsEnabled = !isProcessing;
            PreserveDatesCheckBox.IsEnabled = !isProcessing;
            ClearLogButton.IsEnabled = !isProcessing;

            if (isProcessing)
            {
                ProgressBar.Maximum = Math.Max(1, itemCount);
                ProgressBar.Value = 0;
            }

            ProgressBar.Visibility = isProcessing ? Visibility.Visible : Visibility.Collapsed;
            CancelButton.Visibility = isProcessing ? Visibility.Visible : Visibility.Collapsed;
            CancelButton.IsEnabled = isProcessing;

            UpdateSummaries();
        }

        private void CheckAllFilesButton_Click(object sender, RoutedEventArgs e) => SetAllFilesIncluded(true);

        private void UncheckAllFilesButton_Click(object sender, RoutedEventArgs e) => SetAllFilesIncluded(false);

        private void SetAllFilesIncluded(bool included)
        {
            foreach (ImageFileRow file in _files) file.IsIncluded = included;
            RebuildTagRows();
        }

        private void UpdateSummaries()
        {
            int includedCount = _files.Count(f => f.IsIncluded && !f.HasError);

            FileSummaryTextBlock.Text = _files.Count == 0
                ? "No images loaded."
                : $"{includedCount} of {_files.Count} image(s) checked.";

            TagSummaryTextBlock.Text = $"{_selectedQueries.Count} tag(s) selected | {_tagRows.Count} shown.";

            RemoveTagsButton.IsEnabled = !_isProcessing && _selectedQueries.Count > 0 && includedCount > 0;
        }

        private void ClearLogButton_Click(object sender, RoutedEventArgs e)
        {
            StatusTextBlock.Text = "";
        }

        private void Log(string message)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(() => Log(message));
                return;
            }

            StatusTextBlock.Text += $"[{DateTime.Now:HH:mm:ss}] {message}\n";
            StatusScrollViewer.ScrollToEnd();
        }
    }
}
