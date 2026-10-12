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
    /// <summary>
    /// Pins an open ContentDialog to the exact centre of its XamlRoot for as
    /// long as it is open.
    ///
    /// Why this exists: a ContentDialog is placed by the framework once, when
    /// ShowAsync runs, and the install dialogs keep changing content while they
    /// are open (progress text ticking, a status row appearing, a log expander
    /// opening, an error line appearing). Every one of those changes made the
    /// dialog come out centred and then visibly crawl away from the middle of
    /// the screen, which reads as a broken window. Freezing the size hid most of
    /// it but could not pin the dialog, because the placement itself belongs to
    /// the framework and not to the page.
    ///
    /// So the position is corrected here instead: once per frame, while the
    /// dialog is open, the measured centre of the dialog is compared against
    /// the centre of the XamlRoot and the difference is added to UIElement
    /// Translation. Translation is used on purpose - it is a composition
    /// offset that sits after RenderTransform, so the template's open/close
    /// ScaleTransform animation keeps working and nothing in the dialog's
    /// layout is disturbed. The correction is incremental, so a window resize
    /// or a late content change re-centres over the next frame instead of
    /// jumping.
    ///
    /// Cost while attached is one TransformToVisual per frame on one element;
    /// it stops the moment the dialog closes.
    /// </summary>
    public sealed class DialogCenterLock : IDisposable
    {
        /// <summary>Below this the dialog is already centred (sub-pixel noise).</summary>
        private const double Epsilon = 0.5;

        private readonly FrameworkElement _dialog;
        private bool _disposed;

        private DialogCenterLock(FrameworkElement dialog)
        {
            _dialog = dialog;
            CompositionTarget.Rendering += OnRendering;
        }

        /// <summary>
        /// Starts centring <paramref name="dialog"/>. Dispose to stop. Safe to
        /// call before the dialog is shown: the first frames measure a
        /// zero-sized element and are skipped.
        /// </summary>
        public static DialogCenterLock? Attach(FrameworkElement dialog)
        {
            if (dialog is null) return null;
            return new DialogCenterLock(dialog);
        }

        /// <summary>
        /// Runs a dialog under the lock until it closes, so callers that fire
        /// and forget ShowAsync do not have to hold on to anything.
        /// </summary>
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

            // Not placed yet, or already closing: nothing to correct.
            if (dialog.XamlRoot is null) return;
            if (dialog.ActualWidth <= 0 || dialog.ActualHeight <= 0) return;

            var target = dialog.XamlRoot.Size;

            try
            {
                // Origin of the dialog in XamlRoot coordinates. This includes
                // Translation, which is why the correction below is additive.
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
                // A dialog can be torn down between the null check and the
                // measure. Centring is cosmetic: never let it throw.
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