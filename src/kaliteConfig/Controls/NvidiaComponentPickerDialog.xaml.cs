// ==============================================================================
// Copyright (c) 2026 kaliteConfig
// All rights reserved.
//
// This software and associated documentation files are provided freely for end-users
// to download and use. However, the source code remains strictly proprietary. 
// You may not copy, reproduce, modify, merge, reverse-engineer, publish, distribute, 
// sublicense, or sell copies of the source code in any form, in whole or in part,
// without the express written permission of the copyright holder.
// ==============================================================================
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using kaliteConfig.Services;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;

namespace kaliteConfig.Controls
{
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

        public (IReadOnlyList<NvidiaComponent> Components, bool CleanInstall)? Result { get; private set; }

    public NvidiaComponentPickerDialog(XamlRoot root)
    {
        InitializeComponent();
        XamlRoot = root;

        BeginDownloadPhase();
        PrimaryButtonClick += OnPrimaryClick;
    }

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

    public void FailEarly(string message)
    {
        InstallProgress.Visibility = Visibility.Collapsed;
        StatusText.Visibility = Visibility.Visible;
        StatusText.Text = message;
        PrimaryButtonText = "Close";
        IsPrimaryButtonEnabled = true;
        _installStarted = true;
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
                if (!_vms.Any(v => v.IsSelected))
                {
                    args.Cancel = true;
                    StatusText.Visibility = Visibility.Visible;
                    StatusText.Text = "Select at least one component (the display driver itself cannot be removed).";
                    return;
                }
                args.Cancel = true;
                _installStarted = true;

                Result = (_vms.Select(v => v.Model).ToList(),
                          CleanInstallCheck.IsChecked == true);

                BeginInstallPhase();
                InstallRequested?.Invoke(this, new InstallRequestEventArgs(
                    _vms.Where(v => v.IsSelected).Select(v => v.Id).ToList(),
                    CleanInstallCheck.IsChecked == true));
            }
        }

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
            try { LogScroll.ChangeView(null, LogScroll.ScrollableHeight, null); } catch {  }
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

        public void SetIndeterminate()
        {
            InstallProgress.IsIndeterminate = true;
        }

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
