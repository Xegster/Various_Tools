using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;

namespace ImageStripper
{
    /// <summary>One image in the file list.</summary>
    public sealed class ImageFileRow : INotifyPropertyChanged
    {
        private readonly Action? _onIncludeChanged;
        private bool _isIncluded = true;
        private string _status = "Not scanned yet";

        public ImageFileRow(string fullPath, Action? onIncludeChanged)
        {
            FullPath = fullPath;
            FileName = Path.GetFileName(fullPath);
            _onIncludeChanged = onIncludeChanged;
        }

        public string FullPath { get; }

        public string FileName { get; }

        public List<MetadataTag> Tags { get; private set; } = new();

        public bool HasError { get; private set; }

        public bool IsIncluded
        {
            get => _isIncluded;
            set
            {
                if (_isIncluded == value) return;
                _isIncluded = value;
                OnPropertyChanged();
                _onIncludeChanged?.Invoke();
            }
        }

        public string Status
        {
            get => _status;
            set
            {
                if (_status == value) return;
                _status = value;
                OnPropertyChanged();
            }
        }

        public void ApplyScan(ImageScanResult scan)
        {
            Tags = scan.Tags;
            HasError = scan.Error != null;

            if (scan.Error != null)
            {
                Status = "Could not read: " + scan.Error;
                return;
            }

            string size = scan.PixelWidth > 0 && scan.PixelHeight > 0
                ? $"{scan.PixelWidth} x {scan.PixelHeight}"
                : "unknown size";

            Status = $"{scan.Tags.Count} tag(s) | {size} | {scan.Format}";
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    /// <summary>
    /// One row in the tag grid. The checked state lives in a shared set of query strings
    /// so a tag stays selected while the user moves between files.
    /// </summary>
    public sealed class TagRow : INotifyPropertyChanged
    {
        private readonly ISet<string> _selection;
        private readonly Action? _onSelectionChanged;

        public TagRow(MetadataTag tag, int fileCount, ISet<string> selection, Action? onSelectionChanged)
        {
            Query = tag.Query;
            Group = tag.Group;
            Name = tag.Name;
            ValueText = tag.IsContainer ? "(block)" : tag.Value;
            IsContainer = tag.IsContainer;
            FileCount = fileCount;
            _selection = selection;
            _onSelectionChanged = onSelectionChanged;
        }

        public string Query { get; }

        public string Group { get; }

        public string Name { get; }

        public string ValueText { get; }

        public bool IsContainer { get; }

        /// <summary>How many of the included files carry this tag.</summary>
        public int FileCount { get; }

        public bool IsSelected
        {
            get => _selection.Contains(Query);
            set
            {
                if (value == _selection.Contains(Query)) return;

                if (value) _selection.Add(Query);
                else _selection.Remove(Query);

                OnPropertyChanged();
                _onSelectionChanged?.Invoke();
            }
        }

        /// <summary>Re-reads the shared selection after it was changed from outside this row.</summary>
        public void RefreshSelection() => OnPropertyChanged(nameof(IsSelected));

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
