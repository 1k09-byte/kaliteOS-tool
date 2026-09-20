using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Microsoft.Win32;
using kaliteConfig.Models;
using kaliteConfig.Services;

namespace kaliteConfig.ViewModels;

public class SnipThumbnailItem : ObservableObject
{
    public string Filename { get; set; } = "";
    public string FilePath { get; set; } = "";

    private Microsoft.UI.Xaml.Media.Imaging.BitmapImage? _thumbnail;
    public Microsoft.UI.Xaml.Media.Imaging.BitmapImage? Thumbnail
    {
        get => _thumbnail;
        set => SetProperty(ref _thumbnail, value);
    }

    private string _loadErrorText = "";
    public string LoadErrorText
    {
        get => _loadErrorText;
        private set
        {
            if (SetProperty(ref _loadErrorText, value))
            {
                OnPropertyChanged(nameof(LoadErrorVisibility));
            }
        }
    }

    public Microsoft.UI.Xaml.Visibility LoadErrorVisibility =>
        string.IsNullOrEmpty(_loadErrorText) ? Microsoft.UI.Xaml.Visibility.Collapsed : Microsoft.UI.Xaml.Visibility.Visible;

    public string RelativeTimeText => SnipRelativeTime.Format(FilePath);

    private bool _thumbnailLoading;

    internal static void ThumbLog(string message)
    {
        try
        {
            var dir = System.IO.Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "kaliteConfig");
            System.IO.Directory.CreateDirectory(dir);
            System.IO.File.AppendAllText(System.IO.Path.Combine(dir, "snip-thumb.log"),
                $"{DateTime.Now:HH:mm:ss.fff} [T{System.Environment.CurrentManagedThreadId}] {message}\n");
        }
        catch { }
    }

    public async void EnsureThumbnailAsync(int decodeWidth)
    {
        if (Thumbnail != null || _thumbnailLoading) return;
        _thumbnailLoading = true;
        try
        {
            var img = await SnipGalleryService.LoadThumbnailAsync(FilePath, decodeWidth);
            ThumbLog($"recent '{System.IO.Path.GetFileName(FilePath)}' -> {(img == null ? "NULL" : "ok")}");
            if (img != null) Thumbnail = img;
            else LoadErrorText = "Couldn't load thumbnail.";
        }
        catch (Exception ex)
        {
            ThumbLog($"recent '{System.IO.Path.GetFileName(FilePath)}' EX: {ex.GetType().Name}: {ex.Message}");
            LoadErrorText = $"Couldn't load: {ex.Message}";
        }
        finally { _thumbnailLoading = false; }
    }
}

public partial class GalleryItem : ObservableObject
{
    private readonly SnipViewModel _owner;
    public SnipEntry Entry { get; }

    public string Name => Entry.Name;
    public string FilePath => Entry.FilePath;
    public string SizeText => SnipGalleryQuery.FormatSize(Entry.SizeBytes);
    public string CreatedText => Entry.CreatedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
    public string SourceAppText => string.IsNullOrEmpty(Entry.SourceApp) ? "Unknown app" : Entry.SourceApp;
    public string ResolutionText => Entry.Width > 0 && Entry.Height > 0 ? $"{Entry.Width} \u00d7 {Entry.Height}" : "";
    public string TagsText => Entry.Tags.Count > 0 ? string.Join(", ", Entry.Tags) : "";
    public string TypeText => Entry.Type;
    public string FilePathModifiedTitle => System.IO.Path.GetFileName(FilePath);

    private double _thumbSize = 220;
    public double ThumbSize
    {
        get => _thumbSize;
        set => SetProperty(ref _thumbSize, value);
    }

    public GalleryItem(SnipViewModel owner, SnipEntry entry)
    {
        _owner = owner;
        Entry = entry;
        _thumbSize = owner?.GalleryThumbSize ?? 220;
    }

    private Microsoft.UI.Xaml.Media.Imaging.BitmapImage? _thumbnail;
    public Microsoft.UI.Xaml.Media.Imaging.BitmapImage? Thumbnail
    {
        get => _thumbnail;
        set => SetProperty(ref _thumbnail, value);
    }

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (SetProperty(ref _isSelected, value))
            {
                OnPropertyChanged(nameof(SelectedBorder));
            }
        }
    }

    private bool _isHover;
    public bool IsHover
    {
        get => _isHover;
        set
        {
            if (SetProperty(ref _isHover, value))
            {
                OnPropertyChanged(nameof(StarVisibility));
                OnPropertyChanged(nameof(StarOpacity));
            }
        }
    }

    private bool _isFavorite;
    public bool IsFavorite
    {
        get => _isFavorite;
        set
        {
            if (SetProperty(ref _isFavorite, value))
            {
                Entry.IsFavorite = value;
                OnPropertyChanged(nameof(FavoriteGlyph));
                _ = _owner.SetFavoriteAsync(this, value);
                _owner.OnFavoriteChanged();
            }
        }
    }

    public string FavoriteGlyph => IsFavorite ? "\uE735" : "\uE734";

    public Microsoft.UI.Xaml.Visibility StarVisibility =>
        (IsFavorite || IsHover) ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
    public double StarOpacity => IsFavorite ? 1.0 : 0.6;
    public Microsoft.UI.Xaml.Media.Brush StarBrush =>
        new Microsoft.UI.Xaml.Media.SolidColorBrush(IsFavorite ? Microsoft.UI.Colors.Gold : Microsoft.UI.Colors.White);
    public Microsoft.UI.Xaml.Thickness SelectedBorder =>
        IsSelected ? new Microsoft.UI.Xaml.Thickness(2) : new Microsoft.UI.Xaml.Thickness(0);

    private bool _thumbnailLoading;
    public bool IsThumbnailLoading => _thumbnailLoading;

    public DateTime SortKeyCreatedUtc => Entry.CreatedUtc;

    public async void EnsureThumbnailAsync(int decodeWidth)
    {
        if (Thumbnail != null || _thumbnailLoading) return;
        _thumbnailLoading = true;
        try
        {
            var (img, w, h) = await SnipGalleryService.LoadThumbnailWithSizeAsync(Entry.FilePath, decodeWidth);
            SnipThumbnailItem.ThumbLog($"gallery '{System.IO.Path.GetFileName(Entry.FilePath)}' exists={System.IO.File.Exists(Entry.FilePath)} -> {(img == null ? "NULL" : $"ok {w}x{h}")}");
            if (img != null)
            {
                if (Entry.Width == 0 && w > 0)
                {
                    Entry.Width = w;
                    Entry.Height = h;
                    OnPropertyChanged(nameof(ResolutionText));
                }
                Thumbnail = img;
            }
        }
        catch (Exception ex) { SnipThumbnailItem.ThumbLog($"gallery '{System.IO.Path.GetFileName(Entry.FilePath)}' EX: {ex.GetType().Name}: {ex.Message}"); }
        finally { _thumbnailLoading = false; }
    }

    private bool _isCommandRunning;
    public bool IsCommandRunning
    {
        get => _isCommandRunning;
        set => SetProperty(ref _isCommandRunning, value);
    }

    public void OnEntryChanged()
    {
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(FilePath));
    }

    [RelayCommand]
    private async System.Threading.Tasks.Task OpenAsync()
    {
        var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(Entry.FilePath);
        await Windows.System.Launcher.LaunchFileAsync(file);
    }

    [RelayCommand]
    private async System.Threading.Tasks.Task CopyImageAsync()
    {
        try
        {
            var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(Entry.FilePath);
            var dp = new Windows.ApplicationModel.DataTransfer.DataPackage();
            dp.SetBitmap(Windows.Storage.Streams.RandomAccessStreamReference.CreateFromFile(file));
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dp);
            _owner.NotifyMessage("Copied to clipboard", $"{Name} copied as image.");
        }
        catch (Exception ex)
        {
            _owner.NotifyMessage("Copy failed", ex.Message);
        }
    }

    [RelayCommand]
    private void CopyPath()
    {
        var dp = new Windows.ApplicationModel.DataTransfer.DataPackage();
        dp.SetText(Entry.FilePath);
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dp);
        _owner.NotifyMessage("Path copied", Entry.FilePath);
    }

    [RelayCommand]
    private async System.Threading.Tasks.Task CopyTextAsync()
    {
        await _owner.ExtractTextAsync(this);
    }

    [RelayCommand]
    private async System.Threading.Tasks.Task DeleteAsync()
    {
        await _owner.DeleteItemsAsync(new[] { this });
    }

    [RelayCommand]
    private async System.Threading.Tasks.Task RenameAsync()
    {
        await _owner.RequestRenameAsync(this);
    }

    [RelayCommand]
    private void OpenContainingFolder()
    {
        _owner.OpenContainingFolder(this);
    }

    [RelayCommand]
    private void ToggleFavorite()
    {
        IsFavorite = !IsFavorite;
    }
}

public sealed partial class SnipViewModel : ObservableObject
{
    public System.Collections.ObjectModel.ObservableCollection<SnipThumbnailItem> RecentSnips { get; } = new();
    public System.Collections.ObjectModel.ObservableCollection<GalleryItem> GallerySnips { get; } = new();

    private List<GalleryItem> _itemCache = new();
    private List<SnipEntry> _allEntries = new();

    public SnipViewModel()
    {
    }

    public System.Threading.Tasks.Task RefreshGalleryAsync() => RefreshCoreAsync(true);

    private async System.Threading.Tasks.Task RefreshCoreAsync(bool refreshFilters)
    {
        RecentSnips.Clear();
        SnipGalleryService.CleanupOrphanedMeta();
        _allEntries = await SnipGalleryService.GetAllSnipEntriesAsync();

        await RefreshStorageTextAsync();
        OnPropertyChanged(nameof(AvailableAppOptions));

        await RebuildGalleryAsync();

        var recentsTask = _allEntries
            .OrderByDescending(e => e.CreatedUtc)
            .Take(8)
            .Select(e => new SnipThumbnailItem { Filename = e.Name, FilePath = e.FilePath })
            .ToList();

        foreach (var recent in recentsTask)
        {
            RecentSnips.Add(recent);
        }
    }

    private async System.Threading.Tasks.Task RebuildGalleryAsync()
    {
        OnPropertyChanged(nameof(AvailableAppOptions));

        var app = _filterApp;
        if (app != "All" && !AvailableAppOptions.Contains(app))
        {
            _filterApp = "All";
            OnPropertyChanged(nameof(SelectedAppIndex));
        }

        var filtered = SnipGalleryQuery.Apply(_allEntries, BuildFilterSpec(), _sortMode);

        var newItems = new List<GalleryItem>();
        var cacheByPath = _itemCache.ToDictionary(i => i.Entry.FilePath, i => i, System.StringComparer.OrdinalIgnoreCase);

        foreach (var entry in filtered)
        {
            if (cacheByPath.TryGetValue(entry.FilePath, out var cached))
            {
                cached.Entry.Tags = entry.Tags;
                cached.Entry.IsFavorite = entry.IsFavorite;
                newItems.Add(cached);
            }
            else
            {
                newItems.Add(new GalleryItem(this, entry));
            }
        }
        _itemCache = newItems;

        GallerySnips.Clear();
        foreach (var item in newItems)
        {
            GallerySnips.Add(item);
        }

        foreach (var item in GallerySnips)
        {
            item.IsSelected = false;
        }

        SelectedGalleryItem = null;
        OnPropertyChanged(nameof(StatusBarSnipCountText));
        OnPropertyChanged(nameof(StorageText));

        await System.Threading.Tasks.Task.CompletedTask;
    }

    private SnipFilterSpec BuildFilterSpec()
    {
        return new SnipFilterSpec
        {
            Query = _searchQuery,
            Type = _filterType,
            Format = _filterFormat,
            SourceApp = _filterApp,
            FavoritesOnly = _favoritesOnly,
            Date = _dateFilter,
        };
    }

    private string _searchQuery = "";
    public string SearchQuery
    {
        get => _searchQuery;
        set
        {
            if (SetProperty(ref _searchQuery, value))
            {
                NotifyFiltersChanged();
                _ = RebuildGalleryAsync();
            }
        }
    }
    public List<string> TypeOptions => new() { "All", "Region", "Window", "Fullscreen", "Freeform", "Clipboard", "Imported" };

    private int _typeIndex;
    public int TypeIndex
    {
        get => _typeIndex;
        set
        {
            if (SetProperty(ref _typeIndex, value))
            {
                _filterType = value > 0 && value < TypeOptions.Count ? TypeOptions[value] : "All";
                NotifyFiltersChanged();
                _ = RebuildGalleryAsync();
            }
        }
    }

    public List<string> FormatOptions => new() { "All", "PNG", "JPEG", "WebP", "BMP" };

    private int _formatIndex;
    public int FormatIndex
    {
        get => _formatIndex;
        set
        {
            if (SetProperty(ref _formatIndex, value))
            {
                _filterFormat = value > 0 && value < FormatOptions.Count ? FormatOptions[value] : "All";
                NotifyFiltersChanged();
                _ = RebuildGalleryAsync();
            }
        }
    }

    public List<string> DateOptions => new() { "Any time", "Today", "Last 7 days", "Last 30 days", "This month" };

    private int _dateIndex;
    public int DateIndex
    {
        get => _dateIndex;
        set
        {
            if (SetProperty(ref _dateIndex, value))
            {
                _dateFilter = value switch
                {
                    1 => SnipDateFilter.Today,
                    2 => SnipDateFilter.Last7,
                    3 => SnipDateFilter.Last30,
                    4 => SnipDateFilter.ThisMonth,
                    _ => SnipDateFilter.Any,
                };
                NotifyFiltersChanged();
                _ = RebuildGalleryAsync();
            }
        }
    }

    public List<string> SortOptions => new() { "Newest first", "Oldest first", "Name (A\u2192Z)", "Name (Z\u2192A)", "Largest first", "Smallest first" };

    private int _sortIndex;
    public int SortIndex
    {
        get => _sortIndex;
        set
        {
            if (SetProperty(ref _sortIndex, value))
            {
                _sortMode = value switch
                {
                    1 => SnipSortMode.Oldest,
                    2 => SnipSortMode.NameAsc,
                    3 => SnipSortMode.NameDesc,
                    4 => SnipSortMode.SizeDesc,
                    5 => SnipSortMode.SizeAsc,
                    _ => SnipSortMode.Newest,
                };
                _ = RebuildGalleryAsync();
            }
        }
    }

    public List<string> _appOptions = new();
    public List<string> AppOptions => BuildAppOptions();

    private List<string> BuildAppOptions()
    {
        var apps = SnipGalleryQuery.DistinctSourceApps(_allEntries);
        if (_filterApp != "All" && !apps.Contains(_filterApp))
        {
            _filterApp = "All";
            OnPropertyChanged(nameof(SelectedAppIndex));
        }
        var all = new List<string> { "All apps" };
        all.AddRange(apps);
        return all;
    }

    public List<string> AvailableAppOptions => BuildAppOptions();

    private int _appIndex;
    public int SelectedAppIndex
    {
        get => _appIndex;
        set
        {
            if (SetProperty(ref _appIndex, value))
            {
                var list = AvailableAppOptions;
                _filterApp = value > 0 && value < list.Count ? list[value] : "All";
                NotifyFiltersChanged();
                _ = RebuildGalleryAsync();
            }
        }
    }

    private string _filterType = "All";
    private string _filterFormat = "All";
    private string _filterApp = "All";
    private SnipDateFilter _dateFilter = SnipDateFilter.Any;
    private SnipSortMode _sortMode = SnipSortMode.Newest;

    private bool _favoritesOnly;
    public bool FavoritesOnly
    {
        get => _favoritesOnly;
        set
        {
            if (SetProperty(ref _favoritesOnly, value))
            {
                NotifyFiltersChanged();
                _ = RebuildGalleryAsync();
            }
        }
    }

    private bool _isListView;
    public bool IsListView
    {
        get => _isListView;
        set
        {
            if (SetProperty(ref _isListView, value))
            {
                OnPropertyChanged(nameof(IsGridView));
                OnPropertyChanged(nameof(GridViewVisibility));
                OnPropertyChanged(nameof(ListViewVisibility));
                OnPropertyChanged(nameof(StatusBarSnipCountText));
            }
        }
    }

    public bool IsGridView
    {
        get => !IsListView;
        set => IsListView = !value;
    }
    public Microsoft.UI.Xaml.Visibility GridViewVisibility => IsListView ? Microsoft.UI.Xaml.Visibility.Collapsed : Microsoft.UI.Xaml.Visibility.Visible;
    public Microsoft.UI.Xaml.Visibility ListViewVisibility => IsListView ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
    public Microsoft.UI.Xaml.Visibility SelectionBarVisibility => HasSelection ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

    public Microsoft.UI.Xaml.Visibility DetailsEmptyVisibility =>
        SelectedGalleryItem == null ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
    public Microsoft.UI.Xaml.Visibility DetailsContentVisibility =>
        SelectedGalleryItem == null ? Microsoft.UI.Xaml.Visibility.Collapsed : Microsoft.UI.Xaml.Visibility.Visible;

    public Microsoft.UI.Xaml.Visibility HasActiveFiltersVisibility => HasActiveFilters
        ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

    public bool HasActiveFilters =>
        !string.IsNullOrWhiteSpace(_searchQuery) || _filterType != "All" || _filterFormat != "All"
        || _filterApp != "All" || _favoritesOnly || _dateFilter != SnipDateFilter.Any;

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    public void ClearFilters()
    {
        _searchQuery = "";
        OnPropertyChanged(nameof(SearchQuery));
        _typeIndex = 0; OnPropertyChanged(nameof(TypeIndex));
        _formatIndex = 0; OnPropertyChanged(nameof(FormatIndex));
        _dateIndex = 0; OnPropertyChanged(nameof(DateIndex));
        _appIndex = 0; OnPropertyChanged(nameof(SelectedAppIndex));
        _filterType = "All"; _filterFormat = "All"; _filterApp = "All";
        _dateFilter = SnipDateFilter.Any;
        _favoritesOnly = false; OnPropertyChanged(nameof(FavoritesOnly));
        OnPropertyChanged(nameof(HasActiveFiltersVisibility));
        _ = RebuildGalleryAsync();
    }

    private void NotifyFiltersChanged()
    {
        OnPropertyChanged(nameof(HasActiveFiltersVisibility));
    }

    private GalleryItem? _selectedGalleryItem;
    public GalleryItem? SelectedGalleryItem
    {
        get => _selectedGalleryItem;
        set
        {
            if (SetProperty(ref _selectedGalleryItem, value))
            {
                OnPropertyChanged(nameof(StatusBarSnipCountText));
                OnPropertyChanged(nameof(DetailsEmptyVisibility));
                OnPropertyChanged(nameof(DetailsContentVisibility));
            }
        }
    }

    public string StatusBarRegistrationText => "Hotkey: Active";

    /// <summary>"Blocked" meant: registry value PrintScreenKeyForSnippingEnabled=0,
    /// i.e. Windows Snipping Tool no longer owns PrtScn — kaliteConfig does. Good state.</summary>
    public string StatusBarPrtScnOwner
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Keyboard", false);
                if (key != null)
                {
                    var val = key.GetValue("PrintScreenKeyForSnippingEnabled");
                    if (val is int i && i == 0) return "PrtScn: kaliteConfig";
                }
            }
            catch { }
            return "PrtScn: Windows Snipping Tool";
        }
    }

    public Microsoft.UI.Xaml.Media.Brush StatusBarPrtScnBrush =>
        StatusBarPrtScnOwner == "PrtScn: kaliteConfig"
            ? new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.LightGreen)
            : new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Orange);

    public string StatusBarPrtScnTooltip => StatusBarPrtScnOwner == "PrtScn: kaliteConfig"
        ? "kaliteConfig owns the PrintScreen key; Windows Snipping Tool is unbound. Click to open Snip settings."
        : "Windows Snipping Tool still owns PrtScn. Use 'Override Windows Snipping Tool' in Snip settings to reclaim it.";

    public string StatusBarHijackText => StatusBarPrtScnOwner;

    public string ElevationBadgeText => IsRunningElevated ? "Running as administrator" : "";
    public Microsoft.UI.Xaml.Visibility ElevationBadgeVisibility => IsRunningElevated
        ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

    public static bool IsRunningElevated
    {
        get
        {
            try
            {
                using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
                return new System.Security.Principal.WindowsPrincipal(identity).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }
    }

    private string _storageText = "";
    public string StorageText => _storageText;

    private async System.Threading.Tasks.Task RefreshStorageTextAsync()
    {
        var (count, bytes) = await SnipGalleryService.GetStorageStatsAsync();
        _storageText = $"{SnipGalleryQuery.FormatSize(bytes)} in {count} snips";
        OnPropertyChanged(nameof(StorageText));
    }

    public string StatusBarSnipCountText => $"{GallerySnips.Count} snips";

    public bool IsInstantModeEnabled
    {
        get => SnipSettingsService.Load().InstantMode;
        set
        {
            var s = SnipSettingsService.Load();
            s.InstantMode = value;
            SnipSettingsService.Save(s);
            OnPropertyChanged(nameof(IsInstantModeEnabled));
        }
    }

    public List<string> HotkeyPresets => SnipHotkeyService.Presets.Select(p => p.Label).ToList();

    public int SelectedHotkeyIndex
    {
        get => SnipSettingsService.Load().HotkeyIndex;
        set
        {
            var s = SnipSettingsService.Load();
            s.HotkeyIndex = value;
            SnipSettingsService.Save(s);
            OnPropertyChanged(nameof(SelectedHotkeyIndex));

            var app = (App)Microsoft.UI.Xaml.Application.Current;
            app.Sniper.RebindHotkey(value);
        }
    }

    [RelayCommand]
    public void DisableWindowsSnipping()
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(@"Control Panel\Keyboard", true);
            key?.SetValue("PrintScreenKeyForSnippingEnabled", 0, RegistryValueKind.DWord);
            OnPropertyChanged(nameof(StatusBarHijackText));

            // Re-register our own hotkey now that Windows has let go of PrtScn.
            var s = SnipSettingsService.Load();
            var app = (App)Microsoft.UI.Xaml.Application.Current;
            var (ok, msg) = app.Sniper.RebindHotkey(s.HotkeyIndex);
            if (!ok)
            {
                NotifyMessage("Windows Snipping disabled, hotkey busy", msg + " Sign out/in, then pick a hotkey below.");
                return;
            }
            NotifyMessage("Print Screen reclaimed", "Windows Snipping Tool unbound. Sign out/in (or restart) once, then PrtScn opens kalite Snip.");
        }
        catch (Exception ex)
        {
            NotifyMessage("Override failed", ex.Message);
        }
    }

    [RelayCommand]
    public void CaptureScreen()
    {
        var app = (App)Microsoft.UI.Xaml.Application.Current;
        app.Sniper?.TriggerCapture();
    }

    public void OnFavoriteChanged()
    {
        OnPropertyChanged(nameof(StorageText));
        _ = RebuildGalleryAsync();
    }

    public event System.Action<string, string>? BannerRequested;

    public void NotifyMessage(string title, string content)
    {
        BannerRequested?.Invoke(title, content);
    }

    public async System.Threading.Tasks.Task SetFavoriteAsync(GalleryItem item, bool value)
    {
        var meta = await SnipGalleryService.LoadMetaAsync(item.Entry.FilePath);
        meta ??= new SnipMeta();
        meta.IsFavorite = value;
        item.Entry.IsFavorite = value;
        await SnipGalleryService.SaveMetaAsync(item.Entry.FilePath, meta);
    }

    public void OnSelectionChanged()
    {
        SelectedItems.Clear();
        foreach (var item in GallerySnips)
        {
            if (item.IsSelected) SelectedItems.Add(item);
        }
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(SelectedCountText));
        OnPropertyChanged(nameof(SelectionBarVisibility));
        OnPropertyChanged(nameof(StatusBarSnipCountText));
    }

    public System.Collections.ObjectModel.ObservableCollection<GalleryItem> SelectedItems { get; } = new();
    public bool HasSelection => SelectedItems.Count > 0;
    public string SelectedCountText => $"{SelectedItems.Count} selected";

    public bool IsSelectionModeActive { get; set; }

    public async System.Threading.Tasks.Task DeleteItemsAsync(IEnumerable<GalleryItem> items)
    {
        var list = items.Where(i => i.Entry.FilePath != null).ToList();
        if (list.Count == 0) return;
        var paths = list.Select(i => i.Entry.FilePath).ToList();
        _undoBatch = await SnipGalleryService.DeleteToTrashAsync(paths);
        if (_undoBatch.Count > 0)
        {
            OnPropertyChanged(nameof(HasUndo));
            OnPropertyChanged(nameof(UndoMessage));
            NotifyMessage("Moved to trash", $"{_undoBatch.Count} snip(s) moved. Use Undo to restore.");
        }
        await RefreshGalleryAsync();
    }

    private List<(string Original, string Trash)> _undoBatch = new();
    public bool HasUndo => _undoBatch.Count > 0;
    public string UndoMessage => _undoBatch.Count > 0 ? $"{_undoBatch.Count} snip(s) in trash" : "";

    [RelayCommand]
    public void UndoDelete()
    {
        if (_undoBatch.Count == 0) return;
        SnipGalleryService.UndoDelete(_undoBatch);
        _undoBatch.Clear();
        OnPropertyChanged(nameof(HasUndo));
        OnPropertyChanged(nameof(UndoMessage));
        NotifyMessage("Restored", "The deleted snips were restored.");
        _ = RefreshGalleryAsync();
    }

    [RelayCommand]
    public async System.Threading.Tasks.Task DeleteSelectedAsync()
    {
        await DeleteItemsAsync(SelectedItems.ToList());
    }

    public async System.Threading.Tasks.Task CopySelectedAsync()
    {
        var files = new List<Windows.Storage.StorageFile>();
        foreach (var item in SelectedItems)
        {
            try
            {
                files.Add(await Windows.Storage.StorageFile.GetFileFromPathAsync(item.Entry.FilePath));
            }
            catch { }
        }
        if (files.Count == 0) return;
        var dp = new Windows.ApplicationModel.DataTransfer.DataPackage();
        dp.SetStorageItems(files);
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dp);
        NotifyMessage("Copied", $"{files.Count} file(s) copied to clipboard.");
    }

    public async System.Threading.Tasks.Task ExportSelectedZipAsync(string zipPath)
    {
        var paths = SelectedItems.Select(i => i.Entry.FilePath).Where(System.IO.File.Exists).ToList();
        if (paths.Count == 0) return;
        await SnipGalleryService.ExportZipAsync(paths, zipPath);
        NotifyMessage("Archive saved", zipPath);
    }

    public async System.Threading.Tasks.Task MoveSelectedAsync(string destDir)
    {
        var moved = await SnipGalleryService.MoveFilesAsync(SelectedItems.Select(i => i.Entry.FilePath), destDir);
        NotifyMessage("Moved", $"{moved} snip(s) moved to {destDir}");
        await RefreshGalleryAsync();
    }

    public async System.Threading.Tasks.Task ConvertSelectedAsync(string destDir, string targetExtension)
    {
        int done = 0;
        foreach (var item in SelectedItems.ToList())
        {
            try
            {
                if (await SnipGalleryService.ConvertImageAsync(item.Entry.FilePath, destDir, targetExtension))
                    done++;
            }
            catch { }
        }
        NotifyMessage("Converted", $"{done} image(s) saved as {targetExtension.TrimStart('.')} in {destDir}");
    }

    public System.Threading.Tasks.Task RequestRenameAsync(GalleryItem item)
    {
        RenameRequested?.Invoke(this, item);
        return System.Threading.Tasks.Task.CompletedTask;
    }

    public event System.EventHandler<GalleryItem>? RenameRequested;

    public async System.Threading.Tasks.Task ApplyRenameAsync(GalleryItem item, string newBaseName)
    {
        if (string.IsNullOrWhiteSpace(newBaseName)) return;
        var ext = System.IO.Path.GetExtension(item.Entry.FilePath);
        var safeName = SnipGalleryQuery.SafeFileName(newBaseName, ext);
        var newPath = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(item.Entry.FilePath)!, safeName);
        if (string.Equals(newPath, item.Entry.FilePath, System.StringComparison.OrdinalIgnoreCase)) return;
        if (System.IO.File.Exists(newPath))
        {
            NotifyMessage("Rename failed", "A file with that name already exists.");
            return;
        }
        try
        {
            System.IO.File.Move(item.Entry.FilePath, newPath);
            var metaSrc = item.Entry.FilePath + ".meta.json";
            if (System.IO.File.Exists(metaSrc))
                System.IO.File.Move(metaSrc, newPath + ".meta.json", true);
            var thumb = SnipGalleryService.ThumbPathFor(item.Entry.FilePath);
            if (System.IO.File.Exists(thumb)) { try { System.IO.File.Delete(thumb); } catch { } }
            item.Entry.FilePath = newPath;
            item.Entry.Name = safeName;
            item.OnEntryChanged();
            NotifyMessage("Renamed", safeName);
            await RefreshGalleryAsync();
        }
        catch (Exception ex)
        {
            NotifyMessage("Rename failed", ex.Message);
        }
    }

    public void OpenContainingFolder(GalleryItem item)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("explorer.exe")
            {
                UseShellExecute = true,
                Arguments = $"/select,\"{item.Entry.FilePath}\""
            };
            System.Diagnostics.Process.Start(psi);
        }
        catch { }
    }

    public async System.Threading.Tasks.Task ExtractTextAsync(GalleryItem item)
    {
        try
        {
            var storageFile = await Windows.Storage.StorageFile.GetFileFromPathAsync(item.Entry.FilePath);
            using var stream = await storageFile.OpenAsync(Windows.Storage.FileAccessMode.Read);
            var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(stream);
            var sourceBitmap = await decoder.GetSoftwareBitmapAsync(Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8, Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied);

            var engine = Windows.Media.Ocr.OcrEngine.TryCreateFromUserProfileLanguages();
            if (engine == null)
            {
                NotifyMessage("OCR unavailable", "No OCR language pack installed on this system.");
                return;
            }
            var result = await engine.RecognizeAsync(sourceBitmap);
            var text = result.Text;
            var meta = await SnipGalleryService.LoadMetaAsync(item.Entry.FilePath) ?? new SnipMeta();
            meta.OcrText = text;
            await SnipGalleryService.SaveMetaAsync(item.Entry.FilePath, meta);
            item.Entry.OcrText = text;

            var preview = string.IsNullOrWhiteSpace(text) ? "No text detected." :
                text.Replace("\r", " ").Replace("\n", " ").Trim();
            if (preview.Length > 160) preview = preview[..157] + "...";
            if (string.IsNullOrWhiteSpace(text))
                NotifyMessage("OCR complete", "No text was detected in this image.");
            else
            {
                var dp = new Windows.ApplicationModel.DataTransfer.DataPackage();
                dp.SetText(text);
                Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dp);
                NotifyMessage("Text extracted", $"{preview}");
            }
        }
        catch (Exception ex)
        {
            NotifyMessage("OCR failed", ex.Message);
        }
    }

    public double GalleryThumbSize
    {
        get => SnipSettingsService.Load().GalleryThumbSize;
        set
        {
            var s = SnipSettingsService.Load();
            if (s.GalleryThumbSize == value) return;
            s.GalleryThumbSize = value;
            SnipSettingsService.Save(s);
            foreach (var item in _itemCache) item.ThumbSize = value;
            OnPropertyChanged(nameof(GalleryThumbSize));
        }
    }

    public int AutoDeleteDays
    {
        get => SnipSettingsService.Load().AutoDeleteDays;
        set
        {
            var s = SnipSettingsService.Load();
            if (s.AutoDeleteDays == value) return;
            s.AutoDeleteDays = value;
            SnipSettingsService.Save(s);
            OnPropertyChanged(nameof(AutoDeleteDays));
        }
    }

    public int AutoDeleteIndex
    {
        get => AutoDeleteDays switch { 7 => 1, 30 => 2, 90 => 3, 365 => 4, _ => 0 };
        set
        {
            var days = value switch { 1 => 7, 2 => 30, 3 => 90, 4 => 365, _ => 0 };
            AutoDeleteDays = days;
            OnPropertyChanged(nameof(AutoDeleteIndex));
        }
    }

    public async System.Threading.Tasks.Task RunAutoDeleteAsync()
    {
        var days = AutoDeleteDays;
        if (days <= 0) return;
        var removed = await SnipGalleryService.AutoDeleteOlderThanAsync(days);
        if (removed > 0)
        {
            NotifyMessage("Auto-delete", $"{removed} old snip(s) removed.");
            await RefreshGalleryAsync();
        }
    }
}