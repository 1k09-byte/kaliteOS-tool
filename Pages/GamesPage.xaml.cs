using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Dispatching;
using kaliteConfig.Models;
using kaliteConfig.Services;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace kaliteConfig.Pages;

public sealed partial class GamesPage : Page
{
    public GamesPageViewModel ViewModel { get; } = new();

    public GamesPage()
    {
        GameLibraryService.Log($"GamesPage hero constructor; base={AppContext.BaseDirectory}");
        InitializeComponent();
        GamesList.ItemsSource = ViewModel.Games;
        Loaded += async (_, _) => await LoadPageAsync();
    }

    private async Task LoadPageAsync()
    {
        GameLibraryService.Log("Hero GamesPage load started");
        LoadingRing.IsActive = true;
        EmptyState.Visibility = Visibility.Collapsed;
        StatusText.Text = "Loading installed games…";
        try
        {
            await ViewModel.LoadAsync();
            GameLibraryService.Log($"Hero GamesPage load completed: {ViewModel.Games.Count} games");
        }
        catch (Exception ex)
        {
            GameLibraryService.Log("Hero GamesPage load failed", ex);
            StatusText.Text = $"Games could not be loaded: {ex.Message}";
        }
        finally
        {
            LoadingRing.IsActive = false;
            UpdateUi();
        }
    }

    private void UpdateUi()
    {
        EmptyState.Visibility = !LoadingRing.IsActive && ViewModel.Games.Count == 0
            ? Visibility.Visible : Visibility.Collapsed;
        CountText.Text = ViewModel.Games.Count.ToString();
        StatusText.Text = LoadingRing.IsActive ? "Loading…" : string.Empty;
    }

    private void CoverImage_ImageFailed(object sender, ExceptionRoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not GameModel game) return;
        GameLibraryService.Log($"[ImageFail] Cover URL failed for '{game.Title}': {game.CoverImageUrl} - {e.ErrorMessage}");

        // Retry as a direct file stream - WinUI's Image sometimes throws
        // E_NETWORK_ERROR on file:// URIs even when the file is readable.
        if (TryLoadLocalFileAsync(sender, game).GetAwaiter().GetResult())
        {
            GameLibraryService.Log($"[ImageFail] Stream fallback applied for '{game.Title}'");
            return;
        }

        // Fallback: exe icon extraction.
        var fallback = ExtractExeIcon(game);
        if (!string.IsNullOrEmpty(fallback))
        {
            GameLibraryService.Log($"[ImageFail] Exe-icon fallback applied for '{game.Title}'");
            game.CoverImageUrl = fallback;
            _ = TryLoadLocalFileAsync(sender, game);
            return;
        }

        // Fallback 3: brand-color block with wordmark - never a bare gray box.
        GameLibraryService.Log($"[ImageFail] Wordmark fallback applied for '{game.Title}'");
        if ((sender as FrameworkElement)?.Parent is Grid grid)
        {
            if (grid.Children.OfType<Border>().FirstOrDefault(b => b.Name == "ArtFallback") is Border fb)
            {
                fb.Background = new SolidColorBrush(GameLibraryService.GetLauncherAccent(game.Launcher));
                fb.Visibility = Visibility.Visible;
            }
            if (sender is Image img) img.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>Loads a local file path into the Image via stream, bypassing
    /// the file:// URI path that can fail with E_NETWORK_ERROR.</summary>
    private static async System.Threading.Tasks.Task<bool> TryLoadLocalFileAsync(object sender, GameModel game)
    {
        try
        {
            if (sender is not Image img) return false;
            var path = game.CoverImageUrl.StartsWith("file:///", StringComparison.OrdinalIgnoreCase)
                ? new Uri(game.CoverImageUrl).LocalPath
                : game.CoverImageUrl;
            if (!File.Exists(path)) return false;

            var bytes = await File.ReadAllBytesAsync(path);
            using var stream = new MemoryStream(bytes);
            var bmp = new BitmapImage();
            await bmp.SetSourceAsync(stream.AsRandomAccessStream());
            img.Source = bmp;
            return true;
        }
        catch (Exception ex)
        {
            GameLibraryService.Log($"[ImageFail] Stream load failed for '{game.Title}'", ex);
            return false;
        }
    }

    private static string ExtractExeIcon(GameModel game)
    {
        try
        {
            var exe = Directory.EnumerateFiles(game.InstallLocation, "*.exe", SearchOption.TopDirectoryOnly).FirstOrDefault();
            if (exe is null)
            {
                GameLibraryService.Log($"[ImageFail] No .exe found in '{game.InstallLocation}' for '{game.Title}'");
                return string.Empty;
            }
            var id = "fb-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(game.Id)))[..12];
            var result = (string)typeof(GameLibraryService)
                .GetMethod("ExtractIconCache", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
                .Invoke(null, new object[] { exe, id })!;
            if (string.IsNullOrEmpty(result))
                GameLibraryService.Log($"[ImageFail] Icon extraction returned empty for '{game.Title}' (exe={exe})");
            return result;
        }
        catch (Exception ex)
        {
            GameLibraryService.Log($"[ImageFail] Exe icon fallback threw for '{game.Title}'", ex);
            return string.Empty;
        }
    }

    private static GameModel? GameFrom(object sender) =>
        (sender as FrameworkElement)?.Tag as GameModel ??
        (sender as FrameworkElement)?.DataContext as GameModel;

    private void Launch(GameModel game)
    {
        GameLibraryService.Log($"Launch requested: {game.Title} ({game.Launcher})");
        if (!GameLibraryService.Launch(game))
            StatusText.Text = $"Could not launch {game.Title}";
    }

    private void Play_Click(object sender, RoutedEventArgs e)
    {
        if (GameFrom(sender) is GameModel game) Launch(game);
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        GameLibraryService.Log("Hero GamesPage refresh clicked");
        LoadingRing.IsActive = true;
        EmptyState.Visibility = Visibility.Collapsed;
        try { await ViewModel.RefreshAsync(); }
        catch (Exception ex) { GameLibraryService.Log("Hero GamesPage refresh failed", ex); StatusText.Text = $"Refresh failed: {ex.Message}"; }
        finally
        {
            LoadingRing.IsActive = false;
            UpdateUi();
        }
    }

    private async void AddGame_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker();
            picker.FileTypeFilter.Add(".exe");
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainWindow));
            var file = await picker.PickSingleFileAsync();
            if (file is null) return;

            var dialog = new ContentDialog
            {
                Title = "Add game",
                PrimaryButtonText = "Add",
                CloseButtonText = "Cancel",
                XamlRoot = XamlRoot,
                Content = new TextBox { PlaceholderText = "Game name", Text = Path.GetFileNameWithoutExtension(file.Name) }
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            var title = ((TextBox)dialog.Content).Text.Trim();
            await ViewModel.AddManualAsync(new GameModel
            {
                Id = "manual:" + file.Path,
                Title = string.IsNullOrWhiteSpace(title) ? Path.GetFileNameWithoutExtension(file.Name) : title,
                Launcher = GameLauncher.Manual,
                InstallLocation = Path.GetDirectoryName(file.Path) ?? string.Empty,
                LaunchCommand = file.Path,
                InstalledUtc = DateTime.UtcNow
            });
            UpdateUi();
        }
        catch (Exception ex) { GameLibraryService.Log("Add manual game failed", ex); }
    }
}

public sealed class GamesPageViewModel
{
    private readonly GameLibraryService _service = new();
    public ObservableCollection<GameModel> Games { get; } = new();

    public async Task LoadAsync()
    {
        var games = await _service.LoadAsync().WaitAsync(TimeSpan.FromSeconds(12));
        Apply(games);
    }

    public async Task RefreshAsync()
    {
        var games = await _service.RefreshAsync().WaitAsync(TimeSpan.FromSeconds(12));
        Apply(games);
    }

    public async Task AddManualAsync(GameModel game)
    {
        await _service.AddManualAsync(game);
        Apply(Games.Append(game).ToList());
    }

    private void Apply(System.Collections.Generic.IReadOnlyList<GameModel> games)
    {
        Games.Clear();
        foreach (var game in games.OrderBy(g => g.Title, StringComparer.CurrentCultureIgnoreCase))
            Games.Add(game);
    }
}
