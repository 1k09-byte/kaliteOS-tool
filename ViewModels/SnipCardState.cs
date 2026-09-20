using System;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace kaliteConfig.ViewModels;

/// <summary>
/// ONE card state machine, shared by Recent Snips and the Gallery grid/list/details panel so
/// those surfaces can never disagree about a snip:
///
///  - Loading: a skeleton at the exact final size (no layout shift).
///  - Loaded:  pixels fade in (~150 ms) — the page runs the animation off <see cref="Loaded"/>.
///  - Failed:  an icon + the short reason + Retry, with the full error in the tooltip.
///  - Missing: "File missing" + "Remove from gallery" (the file was deleted outside the app).
///  - Blank:   a "looks blank" badge (protected/DRM captures are legitimately flat: warn, never hide).
///
/// Sizes are requested in PHYSICAL pixels (DIP x XamlRoot.RasterizationScale) and the smallest
/// covering tier is used, so a card re-requests when DPI or the card size changes.
/// </summary>
public sealed class SnipCardState : ObservableObject
{
    public enum Phase { Loading, Loaded, Failed, Missing }

    private readonly Func<int, CancellationToken, Task<Services.SnipGalleryService.SnipThumbnailLoad>> _loader;
    private CancellationTokenSource? _cts;
    private bool _inFlight;
    private double _lastScale = 1.0;
    private double _lastCardPixels;

    public SnipCardState(
        string filePath,
        Func<int, CancellationToken, Task<Services.SnipGalleryService.SnipThumbnailLoad>> loader)
    {
        FilePath = filePath;
        _loader = loader;
    }

    public string FilePath { get; }

    /// <summary>Raised when a thumbnail becomes visible, so the page can fade it in.</summary>
    public event Action? Loaded;

    private Phase _state = Phase.Loading;
    public Phase State
    {
        get => _state;
        private set
        {
            if (SetProperty(ref _state, value)) RaiseDerived();
        }
    }

    private BitmapImage? _image;
    public BitmapImage? Image
    {
        get => _image;
        private set
        {
            if (SetProperty(ref _image, value)) RaiseDerived();
        }
    }

    private string _fullErrorText = "";
    /// <summary>Full failure reason (card tooltip).</summary>
    public string FullErrorText
    {
        get => _fullErrorText;
        private set { if (SetProperty(ref _fullErrorText, value)) OnPropertyChanged(nameof(ShortErrorText)); }
    }

    /// <summary>Short reason shown on the card face.</summary>
    public string ShortErrorText => State == Phase.Missing
        ? "File missing"
        : string.IsNullOrWhiteSpace(_fullErrorText) ? "Preview failed" : _fullErrorText;

    private bool _looksBlank;
    public bool LooksBlank
    {
        get => _looksBlank;
        private set { if (SetProperty(ref _looksBlank, value)) OnPropertyChanged(nameof(BlankBadgeVisibility)); }
    }

    private string _resolutionText = "";
    /// <summary>"1920 x 1080" from the index/meta once known.</summary>
    public string ResolutionText
    {
        get => _resolutionText;
        private set => SetProperty(ref _resolutionText, value);
    }

    /// <summary>Tier actually loaded (256/512/1024).</summary>
    public int RequestedTier { get; private set; }

    private int _pixelWidth;
    private int _pixelHeight;

    private bool _fillMode;
    /// <summary>Optional "Fill" thumbnail mode (cover instead of fit).</summary>
    public bool FillMode
    {
        get => _fillMode;
        set { if (SetProperty(ref _fillMode, value)) RaiseDerived(); }
    }

    // ------------------------------------------------------------- card states

    public bool IsLoading => State == Phase.Loading;
    public bool IsFailed => State == Phase.Failed;
    public bool IsMissing => State == Phase.Missing;

    public Visibility ImageVisibility => Image is null ? Visibility.Collapsed : Visibility.Visible;
    public Visibility SkeletonVisibility =>
        Image is null && State == Phase.Loading ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ErrorVisibility =>
        Image is null && (State is Phase.Failed or Phase.Missing) ? Visibility.Visible : Visibility.Collapsed;
    public Visibility RetryVisibility =>
        Image is null && State == Phase.Failed ? Visibility.Visible : Visibility.Collapsed;
    public Visibility RemoveMissingVisibility =>
        Image is null && State == Phase.Missing ? Visibility.Visible : Visibility.Collapsed;
    public Visibility BlankBadgeVisibility =>
        _looksBlank && Image is not null ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>An image smaller than the card is shown 1:1 (never upscaled with blur).</summary>
    public bool OneToOne => Image is not null && _lastCardPixels > 0
        && _pixelWidth <= _lastCardPixels && _pixelHeight <= _lastCardPixels;

    /// <summary>Fit by default (never crop); "Fill" mode crops to cover.</summary>
    public Stretch ImageStretch => FillMode
        ? Stretch.UniformToFill
        : OneToOne ? Stretch.None : Stretch.Uniform;

    /// <summary>Physical-pixel size expressed in DIPs for the 1:1 case (NaN = auto).</summary>
    public double ImageWidth => OneToOne ? _pixelWidth / (_lastScale <= 0 ? 1 : _lastScale) : double.NaN;
    public double ImageHeight => OneToOne ? _pixelHeight / (_lastScale <= 0 ? 1 : _lastScale) : double.NaN;

    // ------------------------------------------------------------- loading

    /// <summary>
    /// Loads (or re-loads) the thumbnail for a card of <paramref name="cardDipSize"/> DIPs at the
    /// current rasterization scale. Cheap when the already-loaded tier still covers the request.
    /// </summary>
    public async Task EnsureAsync(double cardDipSize, double rasterizationScale, bool force = false)
    {
        var scale = rasterizationScale <= 0 ? 1.0 : rasterizationScale;
        var required = Services.SnipThumbnailService.RequiredPhysicalPixels(cardDipSize, scale);
        var tier = Services.SnipThumbnailService.PickTier(required);

        _lastScale = scale;
        _lastCardPixels = required;

        if (!force && Image is not null && tier <= RequestedTier)
        {
            // Already covered: only the 1:1 / stretch decision can have changed.
            RaiseDerived();
            return;
        }
        if (_inFlight) return;

        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _inFlight = true;

        try
        {
            if (Image is null) State = Phase.Loading;
            var result = await _loader(tier, ct).ConfigureAwait(true);
            if (ct.IsCancellationRequested) return;

            if (result.Ok)
            {
                Image = result.Image;
                _pixelWidth = result.PixelWidth;
                _pixelHeight = result.PixelHeight;
                RequestedTier = result.Tier;
                LooksBlank = result.LooksBlank;
                if (result.Width > 0 && result.Height > 0)
                    ResolutionText = $"{result.Width} \u00d7 {result.Height}";
                FullErrorText = "";
                State = Phase.Loaded;
                Loaded?.Invoke();
            }
            else if (!string.IsNullOrEmpty(result.Error))
            {
                FullErrorText = result.Error;
                State = result.Missing ? Phase.Missing : Phase.Failed;
            }
        }
        catch (OperationCanceledException)
        {
            // Recycled / scrolled away: not an error.
        }
        catch (Exception ex)
        {
            FullErrorText = ex.Message;
            if (Image is null) State = Phase.Failed;
        }
        finally
        {
            _inFlight = false;
        }
    }

    /// <summary>Re-decode from the source, bypassing the cached tiers.</summary>
    public Task RetryAsync(double cardDipSize, double rasterizationScale)
    {
        Services.SnipThumbnailService.Invalidate(FilePath);
        Image = null;
        State = Phase.Loading;
        return EnsureAsync(cardDipSize, rasterizationScale, force: true);
    }

    /// <summary>Called when a card is recycled or scrolled out of view: stops its decode.</summary>
    public void Cancel()
    {
        try { _cts?.Cancel(); } catch { }
    }

    private void RaiseDerived()
    {
        OnPropertyChanged(nameof(IsLoading));
        OnPropertyChanged(nameof(IsFailed));
        OnPropertyChanged(nameof(IsMissing));
        OnPropertyChanged(nameof(ImageVisibility));
        OnPropertyChanged(nameof(SkeletonVisibility));
        OnPropertyChanged(nameof(ErrorVisibility));
        OnPropertyChanged(nameof(RetryVisibility));
        OnPropertyChanged(nameof(RemoveMissingVisibility));
        OnPropertyChanged(nameof(BlankBadgeVisibility));
        OnPropertyChanged(nameof(ShortErrorText));
        OnPropertyChanged(nameof(ImageStretch));
        OnPropertyChanged(nameof(ImageWidth));
        OnPropertyChanged(nameof(ImageHeight));
    }
}
