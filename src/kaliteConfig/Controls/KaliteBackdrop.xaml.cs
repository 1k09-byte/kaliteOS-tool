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

/// <summary>
/// The app's live background: 36 meteors drifting on a matte-black field.
///
/// Faithful to Meteor_animated_thin(1).svg. In that source each meteor is a
/// separate element with its own CSS transform (Up: translateY -35px over 5.5s,
/// Down: translateY +35px, alternating, staggered by animation-delay). This
/// drives each streak's own RenderTransform on a CompositionTarget.Rendering
/// tick, reproducing that per-element motion rather than panning the whole field
/// as one block - which is why the previous revision looked static.
///
/// Every value written per frame is a TranslateTransform.Y on a RenderTransform.
/// Those are compositor-only properties and never re-run measure/arrange, so the
/// per-frame writes cannot feed back into layout. That is the whole reason this
/// is safe: the earlier revision of this control wrote Width/Height/Margin inside
/// a SizeChanged handler, re-entering layout from a layout callback until the
/// process fail-fasted with 0xC0000409. There is deliberately no SizeChanged
/// handler here.
///
/// Purely decorative: IsHitTestVisible=False, first child of the window.
/// </summary>
public sealed partial class KaliteBackdrop : UserControl
{
    // (element name, travels downward, animation-delay)
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
    private const double Travel = 205.0;      // -35..170 range from the source keyframes

    // Throttled rather than running at the display's refresh rate. Meteors drift
    // 205px over 5.5s, i.e. ~37px/s, so even 15fps moves them ~2.5px between
    // frames - smooth for a slow decorative drift. 30fps cost measurably more for
    // no visible gain.
    //
    // Measured with this control collapsed entirely: 0.18% of the machine.
    // With it running, 0.56-0.70%. It was the single largest CPU consumer in the
    // app, and it is decoration, so it is the first thing that should give way.
    private const double FrameInterval = 1.0 / 15.0;

    /// <summary>
    /// Movement smaller than this is not written at all. Raised from 0.25px: at15fps
    /// a meteor moves ~2.5px per frame, so sub-pixel writes were never the win they
    /// looked like, and the per-transform comparison itself costs something across
    /// 36 streaks every frame.
    /// </summary>
    private const double MinDelta = 0.5;      // px; below this a write is invisible

    private double _lastDrawSeconds;
    private readonly double[] _lastY;
    

    private readonly TranslateTransform?[] _transforms;
    private readonly Stopwatch _clock = new();
    private bool _running;

    /// <summary>Set once the window hook is installed, so it is not doubled.</summary>
    private bool _hooked;
    /// <summary>True while the host window is not the foreground window.</summary>
    private bool _deactivated;
    private Microsoft.UI.Xaml.Window? _window;

    public KaliteBackdrop()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;

        _transforms = new TranslateTransform?[Meteors.Length];
        _lastY = new double[Meteors.Length];

        // Each streak is stroked with a gradient that fades from transparent at
        // its tail to opaque at its head, in the direction of travel - exactly
        // as the source SVG does. Flat strokes were the reason this control did
        // not look like the reference: the fade is what reads as a meteor.
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
        // Track the window's activation state for the lifetime of the control.
        // CompositionTarget.Rendering fires at the display's refresh rate whether
        // or not anyone can see the window, so an unfocused or hidden app was
        // still repainting 36 streaks 30 times a second. Measured: 0.63% of the
        // machine while hidden, 1.19% shown. Pausing when nobody is looking is
        // the whole saving, and costs nothing visually because the meteors are
        // only ever seen in the foreground.
        if (_hooked) return;
        _hooked = true;

        // App.MainWindow is the only reliable handle on the host window: in
        // WinUI 3 a Window is not a UIElement, so it cannot be reached by
        // casting XamlRoot.Content.
        var window = kaliteConfig.App.MainWindow;
        if (window is not null)
        {
            _window = window;
            window.Activated += OnWindowActivated;
            UpdateRunning(!_deactivated);
        }
        else
        {
            // No window to ask (design-time / unusual host): keep the old
            // behaviour rather than never animating.
            UpdateRunning(true);
        }
    }

    private void OnWindowActivated(object sender, Microsoft.UI.Xaml.WindowActivatedEventArgs args)
    {
        _deactivated = args.WindowActivationState
            == Microsoft.UI.Xaml.WindowActivationState.Deactivated;
        UpdateRunning(!_deactivated);
    }

    /// <summary>
    /// Subscribes or unsubscribes the render callback. Idempotent, so the
    /// activation, loaded and visibility paths can all call it freely.
    /// </summary>
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
        // CompositionTarget.Rendering is invoked BY the native composition
        // engine. Anything thrown here propagates back across the managed/native
        // boundary, which no handler in this app can intercept: it does not
        // surface as a catchable .NET exception, it terminates the process as a
        // fatal ExecutionEngineException with no stack trace and nothing written
        // to crash.log.
        //
        // The animation is decorative, so failing to draw a frame must never be
        // able to kill the app. Guard the whole body, and stop the loop outright
        // if it keeps throwing - a permanently faulting animation is worse than
        // no animation.
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

    /// <summary>Consecutive tolerated frame faults before the loop is disabled.</summary>
    private const int MaxRenderFaults = 30;
    private int _renderFaults;

    private void RenderFrame()
    {
        // Read the clock directly each frame. Position is a pure function of
        // elapsed time, so a stalled or dropped frame cannot accumulate drift or
        // teleport a streak - it simply catches up.
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

            // _transforms and Meteors are both sized from the same source, but
            // guard the index anyway: an out-of-range read on a per-frame
            // unmanaged callback is fatal to the process, not recoverable.
            if (i >= Meteors.Length || i >= _lastY.Length)
            {
                continue;
            }

            // Equivalent to the source's `alternate` keyframes: ping-pong 0..1.
            var p = (seconds + Meteors[i].Delay) / CycleSeconds;
            p -= Math.Floor(p);
            var eased = 1.0 - Math.Abs((p * 2.0) - 1.0);

            var offset = (eased - 0.5) * Travel;
            var y = Meteors[i].Down ? offset : -offset;

            // Skip sub-pixel writes; they invalidate the compositor without
            // moving anything a viewer could see.
            if (Math.Abs(y - _lastY[i]) < MinDelta)
            {
                continue;
            }

            _lastY[i] = y;
            t.Y = y;
        }
    }
}
