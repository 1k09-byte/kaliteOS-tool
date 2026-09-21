using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Win32;
using kaliteConfig.Models;
using kaliteConfig.Services;

namespace kaliteConfig.ViewModels;

public class SnipThumbnailItem : ObservableObject
{
    public string Filename { get; set; } = "";
    public string FilePath { get; set; } = "";

    /// <summary>Shared card state (skeleton / fade-in / failed / missing / blank badge).</summary>
    public SnipCardState Card { get; }

    public SnipThumbnailItem()
    {
        Card = new SnipCardState(FilePath, Loader);
        Card.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(SnipCardState.Image) or nameof(SnipCardState.ResolutionText))
            {
                OnPropertyChanged(nameof(Thumbnail));
                OnPropertyChanged(nameof(ResolutionText));
            }
        };
    }

    private Task<SnipGalleryService.SnipThumbnailLoad> Loader(int tier, System.Threading.CancellationToken ct) =>
        SnipGalleryService.LoadThumbnailResultAsync(FilePath, tier, ct);

    /// <summary>Kept for compatibility: the same image the card state holds.</summary>
    public Microsoft.UI.Xaml.Media.Imaging.BitmapImage? Thumbnail => Card.Image;

    public string ResolutionText => Card.ResolutionText;

    public string RelativeTimeText => SnipRelativeTime.Format(FilePath);

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

    public async void EnsureThumbnailAsync(double decodeWidth, double rasterizationScale = 1.0)
    {
        try
        {
            await Card.EnsureAsync(decodeWidth, rasterizationScale);
            SnipThumbnailItem.ThumbLog($"recent '{System.IO.Path.GetFileName(FilePath)}' -> {(Card.Image is null ? Card.ShortErrorText : "ok")}");
        }
        catch (Exception ex)
        {
            SnipThumbnailItem.ThumbLog($"recent '{System.IO.Path.GetFileName(FilePath)}' EX: {ex.GetType().Name}: {ex.Message}");
        }
    }

    public void CancelThumbnail() => Card.Cancel();
}

public partial class GalleryItem : ObservableObject
{
    private readonly SnipViewModel _owner;
    public SnipEntry Entry { get; }
    
    public virtual string UniqueId => Entry.FilePath;

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

        Card = new SnipCardState(entry.FilePath, Loader);
        Card.FillMode = owner?.ThumbnailFillMode ?? false;
        Card.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(SnipCardState.Image) or nameof(SnipCardState.ResolutionText))
            {
                OnPropertyChanged(nameof(Thumbnail));
                OnPropertyChanged(nameof(ResolutionText));
            }
        };
    }

    private Task<SnipGalleryService.SnipThumbnailLoad> Loader(int tier, System.Threading.CancellationToken ct) =>
        SnipGalleryService.LoadThumbnailResultAsync(Entry.FilePath, tier, ct);

    /// <summary>Shared card state: Recent Snips and the Gallery render the exact same states.</summary>
    public SnipCardState Card { get; }

    /// <summary>Same image the card state holds (details panel binding).</summary>
    public Microsoft.UI.Xaml.Media.Imaging.BitmapImage? Thumbnail => Card.Image;

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (SetProperty(ref _isSelected, value))
            {
                OnPropertyChanged(nameof(TileBorder));
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
                OnPropertyChanged(nameof(StarBrush));
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
    /// <summary>The tile's single frame: accent when selected, transparent otherwise.
    /// There is exactly one border, so selection can never look doubled.</summary>
    public Microsoft.UI.Xaml.Media.Brush TileBorder
    {
        get
        {
            if (!IsSelected)
                return new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
            try
            {
                var res = Microsoft.UI.Xaml.Application.Current.Resources;
                string key = "AccentFillColorDefaultBrush";
                if (res.TryGetValue(key, out var b) && b is Microsoft.UI.Xaml.Media.Brush brush) return brush;
            }
            catch { }
            return new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.DodgerBlue);
        }
    }

    private bool _thumbnailLoading;
    public bool IsThumbnailLoading => _thumbnailLoading;

    public DateTime SortKeyCreatedUtc => Entry.CreatedUtc;

    /// <summary>The whole card pipeline, in tiers, DPI-aware (see <see cref="SnipCardState"/>).</summary>
    public async void EnsureThumbnailAsync(double cardDipSize, double rasterizationScale = 1.0)
    {
        _thumbnailLoading = true;
        try
        {
            await Card.EnsureAsync(cardDipSize, rasterizationScale);
            if (Card.Image is not null && Entry.Width == 0 && Card.ResolutionText != "")
            {
                var parts = Card.ResolutionText.Split('\u00d7');
                if (parts.Length == 2
                    && int.TryParse(parts[0].Trim(), out var w)
                    && int.TryParse(parts[1].Trim(), out var h))
                {
                    Entry.Width = w;
                    Entry.Height = h;
                    OnPropertyChanged(nameof(ResolutionText));
                }
            }
            SnipThumbnailItem.ThumbLog($"gallery '{System.IO.Path.GetFileName(Entry.FilePath)}' exists={System.IO.File.Exists(Entry.FilePath)} -> {(Card.Image is null ? Card.ShortErrorText : "ok")}");
        }
        catch (Exception ex) { SnipThumbnailItem.ThumbLog($"gallery '{System.IO.Path.GetFileName(Entry.FilePath)}' EX: {ex.GetType().Name}: {ex.Message}"); }
        finally { _thumbnailLoading = false; }
    }

    public void CancelThumbnail() => Card.Cancel();

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

    /// <summary>Full viewer window: zoom/pan on the same Win2D preview the details panel uses.</summary>
    [RelayCommand]
    private void OpenInViewer()
    {
        try
        {
            ((App)Microsoft.UI.Xaml.Application.Current).Sniper.OpenInViewer(Entry.FilePath);
        }
        catch (Exception ex)
        {
            _owner.NotifyMessage("Viewer failed", ex.Message);
        }
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

    public System.Collections.ObjectModel.ObservableCollection<Models.SnipPipeline> Pipelines { get; } = new();

    public SnipViewModel()
    {
        var discordPipe = new Models.SnipPipeline
        {
            Name = "Instant Share (Discord)",
            BoundProcess = "discord.exe",
            HotkeyLabel = "Ctrl + Shift + S"
        };
        discordPipe.Actions.Add(new Models.CopyAction());
        discordPipe.Actions.Add(new Models.SaveAction());
        Pipelines.Add(discordPipe);
    }

    /// <summary>Explicit refresh (the Refresh button): re-reads everything, still merging in place.</summary>
    public System.Threading.Tasks.Task RefreshGalleryAsync() => RefreshCoreAsync(true);

    private string _snipFolderSignature = "";
    private bool _refreshRunning;
    private System.Threading.Tasks.Task? _refreshInFlight;

    /// <summary>
    /// The periodic background poll. Returns true when the folder had actually changed (and the
    /// lists were merged), which is what drives the poll's adaptive interval.
    ///
    /// It first compares a cheap folder fingerprint (names, sizes, write times) and returns
    /// without touching anything when the folder is unchanged, so polling often costs almost
    /// nothing and never produces a visible refresh: no status text, no busy indicator, no
    /// cleared lists. When something did change, the work is merged into the existing items, so
    /// cards keep their decoded thumbnails, the scroll position stays where it is and the
    /// selection (and the details preview) survives.
    /// </summary>
    public async System.Threading.Tasks.Task<bool> RefreshQuietAsync()
    {
        // A poll that lands on top of an explicit refresh just waits for it: the merge it does is
        // the one we were about to ask for, so there is nothing to start and nothing to drop.
        if (_refreshRunning)
        {
            if (_refreshInFlight is { } running)
            {
                try { await running.ConfigureAwait(true); } catch { }
            }
            return false;
        }

        var signature = await SnipGalleryService.GetSnipSignatureAsync().ConfigureAwait(true);
        if (string.Equals(signature, _snipFolderSignature, StringComparison.Ordinal))
        {
            SnipThumbnailItem.ThumbLog("quiet: folder unchanged");
            return false;
        }

        SnipThumbnailItem.ThumbLog($"quiet: folder changed, {_allEntries.Count} entries cached, {GallerySnips.Count} in gallery");
        await RefreshCoreAsync(false).ConfigureAwait(true);
        SnipThumbnailItem.ThumbLog($"quiet: merged to {GallerySnips.Count} gallery / {RecentSnips.Count} recent");
        return true;
    }

    private System.Threading.Tasks.Task RefreshCoreAsync(bool refreshFilters)
    {
        // One refresh at a time, and a second caller joins the one already running instead of
        // getting a silent no-op -- the Refresh button has to mean something even mid-poll.
        // The published task is created before any of the work runs, so a refresh that finishes
        // synchronously cannot clear the field and then have a completed task assigned into it.
        if (_refreshInFlight is { IsCompleted: false } inFlight) return inFlight;

        var completion = new System.Threading.Tasks.TaskCompletionSource(System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);
        _refreshInFlight = completion.Task;
        _ = RunRefreshAsync(refreshFilters, completion);
        return completion.Task;
    }

    private async System.Threading.Tasks.Task RunRefreshAsync(bool refreshFilters, System.Threading.Tasks.TaskCompletionSource completion)
    {
        _refreshRunning = true;
        try
        {
            SnipGalleryService.CleanupOrphanedMeta();
            _allEntries = await SnipGalleryService.GetAllSnipEntriesAsync().ConfigureAwait(true);
            _snipFolderSignature = await SnipGalleryService.GetSnipSignatureAsync().ConfigureAwait(true);

            await RefreshStorageTextAsync().ConfigureAwait(true);
            OnPropertyChanged(nameof(AvailableAppOptions));

            await RebuildGalleryAsync().ConfigureAwait(true);
            MergeRecentSnips();
        }
        finally
        {
            _refreshRunning = false;
            if (ReferenceEquals(_refreshInFlight, completion.Task)) _refreshInFlight = null;
            completion.TrySetResult();
        }
    }

    /// <summary>
    /// Recent Snips is merged the same way as the gallery: an item that is still present is kept
    /// (keeping its decoded thumbnail and its card animations), so a quiet refresh cannot make the
    /// hub flicker either.
    /// </summary>
    /// <summary>How many snips the Recent strip ever shows.</summary>
    public const int MaxRecentSnips = 5;

    private void MergeRecentSnips()
    {
        var byPath = new Dictionary<string, SnipThumbnailItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in RecentSnips)
        {
            if (!string.IsNullOrEmpty(item.FilePath)) byPath[item.FilePath] = item;
        }

        var desired = new List<SnipThumbnailItem>();
        foreach (var entry in _allEntries.OrderByDescending(e => e.CreatedUtc).Take(MaxRecentSnips))
        {
            if (byPath.TryGetValue(entry.FilePath, out var existing))
            {
                desired.Add(existing);
            }
            else
            {
                desired.Add(new SnipThumbnailItem { Filename = entry.Name, FilePath = entry.FilePath });
            }
        }

        SilentListMerge.Sync(RecentSnips, desired, static i => i.FilePath);
        OnPropertyChanged(nameof(StatusBarSnipCountText));
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

        var flatItems = new List<GalleryItem>();
        var cacheByPath = _itemCache.ToDictionary(i => i.Entry.FilePath, i => i, System.StringComparer.OrdinalIgnoreCase);

        foreach (var entry in filtered)
        {
            if (cacheByPath.TryGetValue(entry.FilePath, out var cached) && cached.Entry.SizeBytes == entry.SizeBytes)
            {
                cached.Entry.Tags = entry.Tags;
                cached.Entry.IsFavorite = entry.IsFavorite;
                cached.Entry.SizeBytes = entry.SizeBytes;
                cached.Entry.Width = entry.Width;
                cached.Entry.Height = entry.Height;
                flatItems.Add(cached);
            }
            else
            {
                flatItems.Add(new GalleryItem(this, entry));
            }
        }

        _itemCache = flatItems;

        // Merge rather than Clear()+Add(): the containers for snips that are still here must not
        // be recycled, or a refresh would flash skeletons back on, jump the scroll position and
        // drop the selection (taking the details preview with it).
        SilentListMerge.Sync(GallerySnips, flatItems, static i => i.UniqueId);

        // Only a selection whose snip is actually gone (deleted, or filtered out) clears the
        // details panel; a selection that is still on screen stays exactly as it is.
        if (SelectedGalleryItem is not null && !GallerySnips.Contains(SelectedGalleryItem))
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

    /// <summary>Real hotkey state — this used to be a hard-coded "Hotkey: Active" even when
    /// registration had failed (which is why a dead PrtScn was invisible to the user).</summary>
    public string StatusBarRegistrationText =>
        ((App)Microsoft.UI.Xaml.Application.Current).Sniper?.HotkeyStatusText ?? "Hotkey: not registered";

    public Microsoft.UI.Xaml.Media.Brush StatusBarHotkeyBrush =>
        ((App)Microsoft.UI.Xaml.Application.Current).Sniper?.HotkeyRegistered == true
            ? new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.LightGreen)
            : new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Orange);

    public string StatusBarHotkeyTooltip
    {
        get
        {
            var sniper = ((App)Microsoft.UI.Xaml.Application.Current).Sniper;
            if (sniper is null) return "Capture service unavailable.";
            return sniper.HotkeyRegistered
                ? $"{sniper.HotkeyLabel} opens the capture overlay."
                : $"No capture hotkey could be registered. {sniper.HotkeyError}";
        }
    }

    /// <summary>Re-reads the capture service state (after a rebind or a failed registration).</summary>
    public void RefreshHotkeyStatus()
    {
        OnPropertyChanged(nameof(StatusBarRegistrationText));
        OnPropertyChanged(nameof(StatusBarHotkeyBrush));
        OnPropertyChanged(nameof(StatusBarHotkeyTooltip));
    }

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

    public bool SnipToastEnabled
    {
        get => SnipSettingsService.Load().CaptureToasts;
        set
        {
            var s = SnipSettingsService.Load();
            s.CaptureToasts = value;
            SnipSettingsService.Save(s);
            OnPropertyChanged(nameof(SnipToastEnabled));
        }
    }

    public List<string> HotkeyPresets => SnipHotkeyService.Presets.Select(p => p.Label)
        .Concat(new[] { "Custom…" }).ToList();

    public string CustomHotkeyText
    {
        get
        {
            var s = SnipSettingsService.Load();
            if (s.CustomHotkeyModifiers == 0 && s.CustomHotkeyVk == 0) return "";
            return SnipHotkeyService.FormatLabel(s.CustomHotkeyModifiers, s.CustomHotkeyVk);
        }
    }

    /// <summary>Applies a captured combination: registers first, persists only on success,
    /// warns on conflict and leaves the previous hotkey (and UI selection) untouched.</summary>
    public void ApplyCustomHotkey(uint modifiers, uint vk)
    {
        var app = (App)Microsoft.UI.Xaml.Application.Current;
        var (ok, message) = app.Sniper.TryCustomHotkey(modifiers, vk);
        if (!ok)
        {
            NotifyMessage("Hotkey taken", message + " The previous hotkey is still active.");
            OnPropertyChanged(nameof(SelectedHotkeyIndex)); // revert the preset picker
            return;
        }
        var s = SnipSettingsService.Load();
        s.HotkeyIndex = SnipHotkeyService.CustomIndex;
        s.CustomHotkeyModifiers = modifiers;
        s.CustomHotkeyVk = vk;
        SnipSettingsService.Save(s);
        OnPropertyChanged(nameof(SelectedHotkeyIndex));
        OnPropertyChanged(nameof(CustomHotkeyText));
        RefreshHotkeyStatus();
        NotifyMessage("Custom hotkey active", message);
    }

    public int SelectedHotkeyIndex
    {
        get => SnipSettingsService.Load().HotkeyIndex;
        set
        {
            if (value < 0 || value > SnipHotkeyService.CustomIndex) value = 0;
            var s = SnipSettingsService.Load();
            s.HotkeyIndex = value;
            if (value != SnipHotkeyService.CustomIndex)
            {
                s.CustomHotkeyModifiers = 0;
                s.CustomHotkeyVk = 0;
            }
            SnipSettingsService.Save(s);
            OnPropertyChanged(nameof(SelectedHotkeyIndex));
            OnPropertyChanged(nameof(CustomHotkeyText));

            var app = (App)Microsoft.UI.Xaml.Application.Current;
            var (ok, message) = app.Sniper.RebindHotkey(value);
            RefreshHotkeyStatus();
            if (!ok)
            {
                NotifyMessage("Hotkey unavailable", message);
            }
            else if (!string.IsNullOrEmpty(app.Sniper.HotkeyError))
            {
                // Registered, but on a fallback key: say so instead of pretending it is the chosen one.
                NotifyMessage("Hotkey changed to a free key", message);
            }
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

            // Machine policy as well (needs admin; best effort when elevated).
            string machineNote;
            try
            {
                using var pol = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Policies\Microsoft\TabletPC", true);
                pol?.SetValue("DisableSnippingTool", 1, RegistryValueKind.DWord);
                machineNote = "Machine policy set.";
            }
            catch (Exception ex)
            {
                machineNote = "Machine policy skipped (needs admin): " + ex.Message;
            }

            // Re-register our own hotkey now that Windows has let go of PrtScn.
            var s = SnipSettingsService.Load();
            var app = (App)Microsoft.UI.Xaml.Application.Current;
            var (ok, msg) = app.Sniper.RebindHotkey(s.HotkeyIndex);
            if (!ok)
            {
                NotifyMessage("Windows Snipping disabled, hotkey busy", msg + " Sign out/in, then pick a hotkey below. " + machineNote);
                return;
            }
            NotifyMessage("Print Screen reclaimed", $"Windows Snipping Tool unbound. {machineNote} Sign out/in (or restart) once, then PrtScn opens kalite Snip.");
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

    public System.Collections.ObjectModel.ObservableCollection<SnipThumbnailItem> TrashItems { get; } = new();

    public async System.Threading.Tasks.Task RefreshTrashAsync()
    {
        var entries = await System.Threading.Tasks.Task.Run(() => SnipGalleryService.GetTrashEntries()).ConfigureAwait(true);
        TrashItems.Clear();
        foreach (var e in entries)
            TrashItems.Add(new SnipThumbnailItem { Filename = e.Name, FilePath = e.TrashPath });
    }

    public async System.Threading.Tasks.Task DeleteGalleryPathsAsync(List<string> paths)
    {
        var set = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
        await DeleteItemsAsync(GallerySnips.Where(g => set.Contains(g.Entry.FilePath)).ToList()).ConfigureAwait(true);
    }

    public async System.Threading.Tasks.Task<List<(string FilePath, string Name, long Bytes)>> FindBlankSnipsAsync() =>
        await SnipGalleryService.FindBlankSnipsAsync().ConfigureAwait(true);

    public async System.Threading.Tasks.Task RestoreTrashAsync(List<string> trashPaths)
    {
        int done = await System.Threading.Tasks.Task.Run(() => SnipGalleryService.RestoreFromTrash(trashPaths)).ConfigureAwait(true);
        NotifyMessage("Restored", $"{done} snip(s) moved back to the gallery.");
        await RefreshTrashAsync().ConfigureAwait(true);
        await RefreshGalleryAsync().ConfigureAwait(true);
    }

    public async System.Threading.Tasks.Task DeleteTrashForeverAsync(List<string> trashPaths)
    {
        int done = await System.Threading.Tasks.Task.Run(() => SnipGalleryService.DeleteForever(trashPaths)).ConfigureAwait(true);
        NotifyMessage("Deleted forever", $"{done} snip(s) permanently removed.");
        await RefreshTrashAsync().ConfigureAwait(true);
    }

    public async System.Threading.Tasks.Task EmptyTrashAsync()
    {
        await System.Threading.Tasks.Task.Run(() => SnipGalleryService.PurgeTrash()).ConfigureAwait(true);
        NotifyMessage("Bin emptied", "Everything in the bin was permanently removed.");
        await RefreshTrashAsync().ConfigureAwait(true);
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

    /// <summary>Optional "Fill" card mode: cover the card (crops) instead of fitting it (never crops).</summary>
    public bool ThumbnailFillMode
    {
        get => SnipSettingsService.Load().ThumbnailFillMode;
        set
        {
            var s = SnipSettingsService.Load();
            if (s.ThumbnailFillMode == value) return;
            s.ThumbnailFillMode = value;
            SnipSettingsService.Save(s);
            foreach (var item in _itemCache) item.Card.FillMode = value;
            OnPropertyChanged(nameof(ThumbnailFillMode));
        }
    }

    /// <summary>Retries a card whose thumbnail failed (bypasses the cached tiers).</summary>
    public async Task RetryCardAsync(GalleryItem item, double cardDipSize, double rasterizationScale)
    {
        await item.Card.RetryAsync(cardDipSize, rasterizationScale);
        if (item.Card.Image is null && item.Card.IsMissing)
            NotifyMessage("File missing", $"{item.Name} was deleted or moved outside the app.");
    }

    public async Task RetryCardAsync(SnipThumbnailItem item, double cardDipSize, double rasterizationScale)
    {
        await item.Card.RetryAsync(cardDipSize, rasterizationScale);
        if (item.Card.Image is null && item.Card.IsMissing)
            NotifyMessage("File missing", $"{item.Filename} was deleted or moved outside the app.");
    }

    /// <summary>"Remove from gallery" for a card whose file is gone: drops the list entry (and any
    /// orphaned sidecar), leaving the user's disk alone.</summary>
    public async Task RemoveFromGalleryAsync(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return;
        // Never delete the image here: only the gallery entry.
        try
        {
            var meta = filePath + ".meta.json";
            if (!System.IO.File.Exists(filePath) && System.IO.File.Exists(meta))
                System.IO.File.Delete(meta);
        }
        catch { }

        SnipThumbnailService.DeleteCachedThumbnails(filePath);

        _allEntries = _allEntries.Where(e => !string.Equals(e.FilePath, filePath, StringComparison.OrdinalIgnoreCase)).ToList();
        _itemCache = _itemCache.Where(i => !string.Equals(i.Entry.FilePath, filePath, StringComparison.OrdinalIgnoreCase)).ToList();

        var gallery = GallerySnips.FirstOrDefault(i => string.Equals(i.Entry.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
        if (gallery != null) GallerySnips.Remove(gallery);
        var recent = RecentSnips.FirstOrDefault(i => string.Equals(i.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
        if (recent != null) RecentSnips.Remove(recent);

        if (SelectedGalleryItem is { } sel && string.Equals(sel.Entry.FilePath, filePath, StringComparison.OrdinalIgnoreCase))
            SelectedGalleryItem = null;

        OnPropertyChanged(nameof(StatusBarSnipCountText));
        await RefreshStorageTextAsync();
        NotifyMessage("Removed from gallery", $"{System.IO.Path.GetFileName(filePath)} was already gone; the gallery entry was removed.");
    }

    /// <summary>Re-requests every card at the current DPI (window moved to another monitor, or the
    /// display scale changed): the smallest tier that covers the new physical size is loaded.</summary>
    public void RefreshCardDpi(double rasterizationScale)
    {
        foreach (var item in GallerySnips) _ = item.Card.EnsureAsync(item.ThumbSize, rasterizationScale);
        foreach (var item in RecentSnips) _ = item.Card.EnsureAsync(240, rasterizationScale);
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

    public async System.Threading.Tasks.Task GroupFilesAsync(IEnumerable<GalleryItem> items)
    {
        var list = items?.ToList();
        if (list == null || list.Count < 2) return;

        string baseDir = System.IO.Path.GetDirectoryName(list[0].Entry.FilePath)!;
        string newDir = System.IO.Path.Combine(baseDir, "Screenshots 1");
        int counter = 1;
        while (System.IO.Directory.Exists(newDir) || System.IO.File.Exists(newDir))
        {
            counter++;
            newDir = System.IO.Path.Combine(baseDir, $"Screenshots {counter}");
        }
        System.IO.Directory.CreateDirectory(newDir);

        _undoBatch.Clear();

        foreach (var item in list)
        {
            string newPath = System.IO.Path.Combine(newDir, System.IO.Path.GetFileName(item.Entry.FilePath));
            System.IO.File.Move(item.Entry.FilePath, newPath);
            
            var meta = await SnipGalleryService.LoadMetaAsync(item.Entry.FilePath);
            if (meta != null)
            {
                await SnipGalleryService.SaveMetaAsync(newPath, meta);
                System.IO.File.Delete(System.IO.Path.ChangeExtension(item.Entry.FilePath, ".xml"));
            }

            _undoBatch.Add((item.Entry.FilePath, newPath));
        }

        OnPropertyChanged(nameof(HasUndo));
        NotifyMessage("Grouped selected files", $"Created {System.IO.Path.GetFileName(newDir)}. Use Undo to unpack.");

        await RefreshCoreAsync(false);
        ClearSelection();
    }

    public void ClearSelection()
    {
        SelectedItems.Clear();
        foreach (var item in GallerySnips) item.IsSelected = false;
        OnSelectionChanged();
    }

    public async System.Threading.Tasks.Task MergeIntoFolderAsync(GalleryItem target, GalleryItem source)
    {
        if (target == source) return;

        _undoBatch.Clear();

        // Target is a single file. We need to create a new folder "Screenshots X" right here.
        string baseDir = System.IO.Path.GetDirectoryName(target.Entry.FilePath)!;
        string newDir = System.IO.Path.Combine(baseDir, "Screenshots 1");
        int counter = 1;
        while (System.IO.Directory.Exists(newDir) || System.IO.File.Exists(newDir))
        {
            counter++;
            newDir = System.IO.Path.Combine(baseDir, $"Screenshots {counter}");
        }
        System.IO.Directory.CreateDirectory(newDir);
        
        // Move target into this new folder
        string targetNewPath = System.IO.Path.Combine(newDir, System.IO.Path.GetFileName(target.Entry.FilePath));
        System.IO.File.Move(target.Entry.FilePath, targetNewPath);
        
        var metaT = await SnipGalleryService.LoadMetaAsync(target.Entry.FilePath);
        if (metaT != null)
        {
            await SnipGalleryService.SaveMetaAsync(targetNewPath, metaT);
            System.IO.File.Delete(System.IO.Path.ChangeExtension(target.Entry.FilePath, ".xml"));
        }
        
        _undoBatch.Add((target.Entry.FilePath, targetNewPath));

        string targetDir = newDir;

        var sourcesToMove = new[] { source };
        foreach (var s in sourcesToMove.ToList())
        {
            string sNewPath = System.IO.Path.Combine(targetDir, System.IO.Path.GetFileName(s.Entry.FilePath));
            if (s.Entry.FilePath != sNewPath)
            {
                System.IO.File.Move(s.Entry.FilePath, sNewPath);
                
                var metaS = await SnipGalleryService.LoadMetaAsync(s.Entry.FilePath);
                if (metaS != null)
                {
                    await SnipGalleryService.SaveMetaAsync(sNewPath, metaS);
                    System.IO.File.Delete(System.IO.Path.ChangeExtension(s.Entry.FilePath, ".xml"));
                }
                
                _undoBatch.Add((s.Entry.FilePath, sNewPath));
            }
        }
        
        // Show Undo toast
        OnPropertyChanged(nameof(HasUndo));
        NotifyMessage("Merged into folder", $"Created grouping in {System.IO.Path.GetFileName(targetDir)}. Use Undo to unpack.");

        await RefreshCoreAsync(false);
    }

    public async System.Threading.Tasks.Task MoveToRootAsync(IEnumerable<GalleryItem> items)
    {
        var list = items?.ToList();
        if (list == null || list.Count == 0) return;

        string rootDir = SnipGalleryService.GetSnipsDirectory();
        
        _undoBatch.Clear();

        foreach (var item in list)
        {
            string newPath = System.IO.Path.Combine(rootDir, System.IO.Path.GetFileName(item.Entry.FilePath));
            if (item.Entry.FilePath != newPath)
            {
                System.IO.File.Move(item.Entry.FilePath, newPath);
                
                var meta = await SnipGalleryService.LoadMetaAsync(item.Entry.FilePath);
                if (meta != null)
                {
                    await SnipGalleryService.SaveMetaAsync(newPath, meta);
                    System.IO.File.Delete(System.IO.Path.ChangeExtension(item.Entry.FilePath, ".xml"));
                }
                
                _undoBatch.Add((item.Entry.FilePath, newPath));
            }
        }

        OnPropertyChanged(nameof(HasUndo));
        NotifyMessage("Moved to Gallery", $"Moved {list.Count} item(s) back to the main gallery. Use Undo to revert.");

        await RefreshCoreAsync(false);
        ClearSelection();
    }
}