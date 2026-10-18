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
using System;
using System.Numerics;
using System.Threading.Tasks;
using Windows.Foundation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace kaliteConfig.Services
{
    public sealed class DialogCenterLock : IDisposable
    {
        private const double Epsilon = 0.5;

        private readonly FrameworkElement _dialog;
        private bool _disposed;

        private DialogCenterLock(FrameworkElement dialog)
        {
            _dialog = dialog;
            CompositionTarget.Rendering += OnRendering;
        }

        public static DialogCenterLock? Attach(FrameworkElement dialog)
        {
            if (dialog is null) return null;
            return new DialogCenterLock(dialog);
        }

        public static async Task KeepCenteredAsync(ContentDialog dialog)
        {
            if (dialog is null) return;
            using var centerLock = Attach(dialog);
            try
            {
                await dialog.ShowAsync();
            }
            finally
            {
                centerLock?.Dispose();
            }
        }

        private void OnRendering(object? sender, object args)
        {
            if (_disposed) return;

            var dialog = _dialog;

            if (dialog.XamlRoot is null) return;
            if (dialog.ActualWidth <= 0 || dialog.ActualHeight <= 0) return;

            var target = dialog.XamlRoot.Size;

            try
            {
                var origin = dialog.TransformToVisual(null).TransformPoint(default(Point));

                double centerX = origin.X + dialog.ActualWidth / 2;
                double centerY = origin.Y + dialog.ActualHeight / 2;

                double dx = target.Width / 2 - centerX;
                double dy = target.Height / 2 - centerY;

                if (Math.Abs(dx) < Epsilon && Math.Abs(dy) < Epsilon) return;

                var translation = dialog.Translation;
                dialog.Translation = new Vector3(translation.X + (float)dx, translation.Y + (float)dy, translation.Z);
            }
            catch (Exception)
            {
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            CompositionTarget.Rendering -= OnRendering;
        }
    }
}