using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Threading.Tasks;

namespace kaliteConfig.Pages
{
    public sealed partial class TroubleshootingPage : Page
    {
        private readonly Services.RecoveryService _recovery = new();

        public TroubleshootingPage()
        {
            this.InitializeComponent();
        }

        private void OnLoaded(object sender, RoutedEventArgs e) => RefreshSafeState();

        private void RefreshSafeState()
        {
            bool armed = false;
            try { armed = _recovery.IsSafeBootArmed(); } catch { }
            SafeStateText.Text = armed
                ? "Safe mode is currently armed: every restart uses safe mode."
                : "Safe mode is not armed: Windows starts normally.";
        }

        private void Firmware_Click(object sender, RoutedEventArgs e) =>
            Run(() => _recovery.RebootToFirmware());

        private void SafeOnce_Click(object sender, RoutedEventArgs e) =>
            Run(() => _recovery.RebootToSafeModeOnce());

        private void SafeAlways_Click(object sender, RoutedEventArgs e) =>
            Run(() => _recovery.RebootToSafeModeAlways());

        private void NormalStartup_Click(object sender, RoutedEventArgs e) =>
            Run(() => _recovery.RestoreNormalStartup());

        private void Run(Func<Services.RecoveryResult> work)
        {
            Show(InfoBarSeverity.Informational, "Working…");
            Task.Run(work).ContinueWith(t =>
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (t.IsFaulted || t.Result is null)
                        Show(InfoBarSeverity.Error, "Something went wrong: " + (t.Exception?.GetBaseException().Message ?? "unknown error."));
                    else
                        Show(t.Result.Ok ? InfoBarSeverity.Success : InfoBarSeverity.Error, t.Result.Message);
                    RefreshSafeState();
                });
            });
        }

        private void Show(InfoBarSeverity severity, string message)
        {
            StatusBar.Severity = severity;
            StatusBar.Message = message;
            StatusBar.IsOpen = true;
        }
    }
}
