// ==============================================================================
// Copyright (c) 2026 kaliteConfig
// All rights reserved.
// ==============================================================================
using System;
using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace kaliteConfig.Controls;

public sealed partial class KaliteBackdrop : UserControl
{
    private static readonly (string Name, bool Down, double Delay)[] Meteors =
    {
        ("m0", false, 0.0f),
        ("m1", false, 4.8f),
        ("m2", true, 0.0f),
        ("m3", true, 4.1f),
        ("m4", false, 4.1f),
        ("m5", true, 2.7f),
        ("m6", false, 3.4f),
        ("m7", true, 1.3f),
        ("m8", false, 2.7f),
        ("m9", true, 5.4f),
        ("m10", true, 4.0f),
        ("m11", false, 2.0f),
        ("m12", true, 2.6f),
        ("m13", true, 1.2f),
        ("m14", true, 5.3f),
        ("m15", false, 1.3f),
        ("m16", false, 0.6f),
        ("m17", true, 3.9f),
        ("m18", true, 2.5f),
        ("m19", false, 5.4f),
        ("m20", false, 4.7f),
        ("m21", false, 4.0f),
        ("m22", false, 3.3f),
        ("m23", true, 1.1f),
        ("m24", false, 2.6f),
        ("m25", false, 1.9f),
        ("m26", true, 5.2f),
        ("m27", true, 3.8f),
        ("m28", true, 2.4f),
        ("m29", true, 1.0f),
        ("m30", true, 5.1f),
        ("m31", true, 3.7f),
        ("m32", false, 1.2f),
        ("m33", true, 2.3f),
        ("m34", true, 0.9f),
        ("m35", true, 5.0f),
    };

    private const double CycleSeconds = 5.5;
    private const double Travel = 205.0;

    private const double FrameInterval = 1.0 / 15.0;

    private const double MinDelta = 0.5;

    private double _lastDrawSeconds;
    private readonly double[] _lastY;
    

    private readonly TranslateTransform?[] _transforms;
    private readonly Stopwatch _clock = new();
    private bool _running;

    private bool _hooked;
    private bool _deactivated;
    private Microsoft.UI.Xaml.Window? _window;

    public KaliteBackdrop()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;

        _transforms = new TranslateTransform?[Meteors.Length];
        _lastY = new double[Meteors.Length];

        var up = (Brush)Resources["MeteorUp"];
        var down = (Brush)Resources["MeteorDown"];

        for (var i = 0; i < Meteors.Length; i++)
        {
            if (FindName(Meteors[i].Name) is Line line)
            {
                line.Stroke = Meteors[i].Down ? down : up;

                var t = new TranslateTransform();
                line.RenderTransform = t;
                _transforms[i] = t;
            }
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_hooked) return;
        _hooked = true;

        var window = kaliteConfig.App.MainWindow;
        if (window is not null)
        {
            _window = window;
            window.Activated += OnWindowActivated;
            UpdateRunning(!_deactivated);
        }
        else
        {
            UpdateRunning(true);
        }
    }

    private void OnWindowActivated(object sender, Microsoft.UI.Xaml.WindowActivatedEventArgs args)
    {
        _deactivated = args.WindowActivationState
            == Microsoft.UI.Xaml.WindowActivationState.Deactivated;
        UpdateRunning(!_deactivated);
    }

    private void UpdateRunning(bool shouldRun)
    {
        if (shouldRun == _running) return;

        _running = shouldRun;
        if (shouldRun)
        {
            _clock.Restart();
            _lastDrawSeconds = double.NegativeInfinity;
            CompositionTarget.Rendering += OnRendering;
        }
        else
        {
            CompositionTarget.Rendering -= OnRendering;
            _clock.Stop();
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        UpdateRunning(false);

        if (_window is not null)
        {
            _window.Activated -= OnWindowActivated;
            _window = null;
        }
        _hooked = false;
    }

    private void OnRendering(object? sender, object e)
    {
        try
        {
            RenderFrame();
        }
        catch (Exception)
        {
            if (++_renderFaults >= MaxRenderFaults)
            {
                UpdateRunning(false);
            }
        }
    }

    private const int MaxRenderFaults = 30;
    private int _renderFaults;

    private void RenderFrame()
    {
        var seconds = _clock.Elapsed.TotalSeconds;
        if (seconds - _lastDrawSeconds < FrameInterval)
        {
            return;
        }

        _lastDrawSeconds = seconds;

        for (var i = 0; i < _transforms.Length; i++)
        {
            var t = _transforms[i];
            if (t is null)
            {
                continue;
            }

            if (i >= Meteors.Length || i >= _lastY.Length)
            {
                continue;
            }

            var p = (seconds + Meteors[i].Delay) / CycleSeconds;
            p -= Math.Floor(p);
            var eased = 1.0 - Math.Abs((p * 2.0) - 1.0);

            var offset = (eased - 0.5) * Travel;
            var y = Meteors[i].Down ? offset : -offset;

            if (Math.Abs(y - _lastY[i]) < MinDelta)
            {
                continue;
            }

            _lastY[i] = y;
            t.Y = y;
        }
    }
}
