using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using kaliteConfig.Models;
using kaliteConfig.Services;
using kaliteConfig.ViewModels;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace kaliteConfig.Pages;

public sealed partial class NvidiaSettings3DPage : Page
{
    public Nvidia3DSettingsViewModel Vm { get; } = new();
    private bool _switching;

    public NvidiaSettings3DPage()
    {
        InitializeComponent();
        Loaded += async (_, _) => await Vm.RefreshAsync();
    }

    private async void ThreeDTabView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_switching) return;
        _switching = true;
        try
        {
            Vm.IsProgram = ThreeDTabView.SelectedIndex == 1;
            ProgramPicker.Visibility = Vm.IsProgram ? Visibility.Visible : Visibility.Collapsed;
            AddProgramButton.Visibility = Vm.IsProgram ? Visibility.Visible : Visibility.Collapsed;
            await Vm.LoadRowsAsync();
        }
        finally { _switching = false; }
    }

    private async void AddProgram_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".exe");
        var hwnd = WindowNative.GetWindowHandle(App.MainWindow);
        InitializeWithWindow.Initialize(picker, hwnd);
        var file = await picker.PickSingleFileAsync();
        if (file is null) return;
        Vm.SelectedApplication = new Nvidia3DApplication(file.Path, Path.GetFileNameWithoutExtension(file.Name), "");
        await Vm.LoadRowsAsync();
    }

    private async void Apply3D_Click(object sender, RoutedEventArgs e)
    {
        if (!Vm.IsProgram)
        {
            var dialog = new ContentDialog
            {
                Title = "Apply global 3D settings?",
                Content = "These settings affect every application without its own profile. A full driver profile backup will be saved first.",
                PrimaryButtonText = "Apply",
                CloseButtonText = "Cancel",
                XamlRoot = XamlRoot
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        }
        await Vm.ApplyCommand.ExecuteAsync(null);
    }

    private async void Restore3D_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            Title = "Restore settings?",
            Content = Vm.IsProgram ? "Reset the selected program profile to inherit driver defaults? A full driver database backup will be saved first." : "The current driver database will be backed up before restoring defaults.",
            PrimaryButtonText = "Restore",
            CloseButtonText = "Cancel",
            XamlRoot = XamlRoot
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary) await Vm.RestoreCommand.ExecuteAsync(null);
    }

    private void Reset3DRow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: Nvidia3DSettingRowViewModel row }) row.Value = Vm.IsProgram ? uint.MaxValue : row.DefaultValue;
    }
}
