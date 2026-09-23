using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using kaliteConfig.Services;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace kaliteConfig.Controls
{
    /// <summary>Bindable wrapper for a component row in the picker.</summary>
    public sealed class NvidiaComponentVm : INotifyPropertyChanged
    {
        private readonly NvidiaComponent _c;
        public NvidiaComponentVm(NvidiaComponent c) { _c = c; _isSelected = c.IsSelected; }

        public NvidiaComponent Model => _c;
        public string Id => _c.Id;
        public string DisplayName => _c.Name + (_c.IsLocked ? "  (required)" : "");
        public string Description => _c.Description;
        public string SizeText => _c.SizeText;
        public bool IsEnabled => !_c.IsLocked;

        private bool _isSelected;
        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected == value) return;
                _isSelected = value;

                // Write through to the parsed component: the install pipeline
                // reads Model.IsSelected to build the exclusion set, so a
                // checkbox that only updated this wrapper was invisible to it
                // and every component installed regardless of the selection.
                _c.IsSelected = value;

                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
                SelectionChanged?.Invoke();
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        internal event Action? SelectionChanged;
    }

    public sealed partial class NvidiaComponentPickerDialog : ContentDialog
    {
        private readonly List<NvidiaComponentVm> _vms = new();
        private bool _installStarted;

        /// <summary>Selection + clean-install flag once the user confirmed; null while choosing/cancelled.</summary>
        public (IReadOnlyList<NvidiaComponent> Components, bool CleanInstall)? Result { get; private set; }

    public NvidiaComponentPickerDialog(XamlRoot root)
    {
        InitializeComponent();
        XamlRoot = root;

        // Start in the download/verify phase: components aren't known until the
        // package is downloaded, verified and extracted. BeginDownloadPhase()
        // + SetVerification() + SetComponents() fill this in as the pipeline
        // advances, so the user sees progress from the moment they confirm.
        BeginDownloadPhase();
        PrimaryButtonClick += OnPrimaryClick;
    }

    /// <summary>Dialog starts here: primary disabled, progress bar running.</summary>
    public void BeginDownloadPhase()
    {
        _installStarted = false;
        IsPrimaryButtonEnabled = false;
        PrimaryButtonText = "Preparing…";
        ComponentsList.IsEnabled = false;
        InstallProgress.Visibility = Visibility.Visible;
        InstallProgress.IsIndeterminate = true;
        StatusText.Visibility = Visibility.Visible;
        StatusText.Text = "Downloading the official package from nvidia.com…";
    }

    public void SetVerification(PackageVerificationInfo verification)
    {
        VerFileName.Text = $"File: {verification.FileName}";
        VerSize.Text = $"Size: {verification.SizeBytes / (1024.0 * 1024):#,0} MB";
        VerSha.Text = $"SHA-256: {verification.Sha256}";
        VerSig.Text = verification.SignatureValid
            ? $"Signature: valid - {verification.SignatureSubject}"
            : $"Signature: INVALID - {verification.SignatureError}";
    }

    /// <summary>Called once the package is extracted and setup.cfg is parsed.</summary>
    public void SetComponents(IReadOnlyList<NvidiaComponent> components)
    {
        _vms.Clear();
        foreach (var c in components)
        {
            var vm = new NvidiaComponentVm(c);
            vm.SelectionChanged += UpdateFootprint;
            _vms.Add(vm);
        }
        ComponentsList.ItemsSource = _vms;
        ComponentsList.IsEnabled = true;

        InstallProgress.Visibility = Visibility.Collapsed;
        StatusText.Visibility = Visibility.Collapsed;
        PrimaryButtonText = "Install selected";
        IsPrimaryButtonEnabled = true;

        UpdateFootprint();
    }

    /// <summary>Terminal failure before component selection (download/verify/extract).</summary>
    public void FailEarly(string message)
    {
        InstallProgress.Visibility = Visibility.Collapsed;
        StatusText.Visibility = Visibility.Visible;
        StatusText.Text = message;
        PrimaryButtonText = "Close";
        IsPrimaryButtonEnabled = true;
        _installStarted = true; // primary just closes
    }

        private void UpdateFootprint()
        {
            var sel = _vms.Where(v => v.IsSelected).ToList();
            long bytes = sel.Sum(v => v.Model.ApproxSizeBytes);
            FootprintText.Text =
                $"{sel.Count} component{(sel.Count == 1 ? "" : "s")} selected · ~{bytes / (1024.0 * 1024):#,0} MB on disk";
        }

        private void OnPrimaryClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
        {
            if (!_installStarted)
            {
                // Refuse an empty install: with every removable component unticked the
                // rewritten package would fail inside NVIDIA's installer with a
                // cryptic error. Stay in the dialog instead.
                if (!_vms.Any(v => v.IsSelected))
                {
                    args.Cancel = true;
                    StatusText.Visibility = Visibility.Visible;
                    StatusText.Text = "Select at least one component (the display driver itself cannot be removed).";
                    return;
                }
                // First activation: keep the dialog open and run the install.
                args.Cancel = true;
                _installStarted = true;

                // EVERY component is handed over with its final selection -
                // passing only the ticked ones left the caller with nothing to
                // exclude, which is why unchecked extras still installed.
                Result = (_vms.Select(v => v.Model).ToList(),
                          CleanInstallCheck.IsChecked == true);

                BeginInstallPhase();
                InstallRequested?.Invoke(this, new InstallRequestEventArgs(
                    _vms.Where(v => v.IsSelected).Select(v => v.Id).ToList(),
                    CleanInstallCheck.IsChecked == true));
            }
            // After install completes the dialog closes normally.
        }

        /// <summary>Raised once when the user confirms the component selection.</summary>
        public event EventHandler<InstallRequestEventArgs>? InstallRequested;

        public sealed class InstallRequestEventArgs : EventArgs
        {
            public IReadOnlyList<string> ComponentIds { get; }
            public bool CleanInstall { get; }
            public InstallRequestEventArgs(IReadOnlyList<string> ids, bool clean)
            { ComponentIds = ids; CleanInstall = clean; }
        }

        private void BeginInstallPhase()
        {
            IsPrimaryButtonEnabled = false;
            CleanInstallCheck.IsEnabled = false;
            ComponentsList.IsEnabled = false;
            InstallProgress.Visibility = Visibility.Visible;
            InstallProgress.IsIndeterminate = true;
            StatusText.Visibility = Visibility.Visible;
            LogExpander.Visibility = Visibility.Visible;
            PrimaryButtonText = "Installing…";
        }

        public void ReportStatus(string message)
        {
            StatusText.Text = message;
            LogText.Text += message + Environment.NewLine;
        }

        public void ReportDownloadProgress(double percent)
        {
            if (InstallProgress.IsIndeterminate && percent > 0)
            {
                InstallProgress.IsIndeterminate = false;
                PrimaryButtonText = "Downloading…";
            }
            InstallProgress.Value = Math.Clamp(percent, 0, 100);
        }

        /// <summary>Back to indeterminate (verify/extract phases have no percentage).</summary>
        public void SetIndeterminate()
        {
            InstallProgress.IsIndeterminate = true;
        }

        /// <summary>
        /// Finishes the flow. success + rebootRequired → primary becomes Close,
        /// secondary becomes "Restart now" (reboot is never automatic).
        /// </summary>
        public void CompleteInstall(bool success, bool rebootRequired, string message)
        {
            InstallProgress.IsIndeterminate = false;
            InstallProgress.Value = success ? 100 : InstallProgress.Value;
            ReportStatus(message);

            if (success)
            {
                PrimaryButtonText = "Close";
                IsPrimaryButtonEnabled = true;
                if (rebootRequired)
                {
                    SecondaryButtonText = "Restart now";
                    SecondaryButtonClick += (_, _) =>
                    {
                        // Explicit consent path only - never an automatic reboot.
                        // Elevation is a real UAC prompt; cancel = no restart.
                        var psi = new System.Diagnostics.ProcessStartInfo("shutdown", "/r /t 0")
                        { UseShellExecute = true, Verb = "runas" };
                        try { System.Diagnostics.Process.Start(psi); } catch { }
                    };
                }
            }
            else
            {
                PrimaryButtonText = "Close";
                CloseButtonText = "";
                IsPrimaryButtonEnabled = true;
            }
        }
    }
}
