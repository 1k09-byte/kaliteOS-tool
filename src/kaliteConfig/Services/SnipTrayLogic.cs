using System;
using System.Drawing;

namespace kaliteConfig.Services;

/// <summary>
/// A pure, UI-free logic class to manage gesture states for the Snip Tray (Hold, Swipe, Drag).
/// </summary>
public class SnipTrayLogic
{
    public enum GestureState { None, Holding, Dragging, Swiping }

    public double SwipeDistanceThreshold = 100.0;
    public double DragDistanceThreshold = 10.0;
    public TimeSpan HoldTimeThreshold = TimeSpan.FromMilliseconds(300);

    private PointF _startPoint;
    private DateTime _startTime;
    private bool _pointerDown;
    public GestureState CurrentState { get; private set; } = GestureState.None;

    public void PointerPressed(float x, float y, DateTime time)
    {
        _pointerDown = true;
        _startPoint = new PointF(x, y);
        _startTime = time;
        CurrentState = GestureState.None;
    }

    public GestureState PointerMoved(float x, float y, DateTime time)
    {
        if (!_pointerDown) return GestureState.None;

        float dx = x - _startPoint.X;
        float dy = y - _startPoint.Y;
        double distance = Math.Sqrt(dx * dx + dy * dy);

        if (distance > SwipeDistanceThreshold && (time - _startTime) < HoldTimeThreshold)
        {
            CurrentState = GestureState.Swiping;
            return CurrentState;
        }

        if (distance > DragDistanceThreshold)
        {
            if (CurrentState != GestureState.Swiping)
                CurrentState = GestureState.Dragging;
        }
        else if (distance <= DragDistanceThreshold && (time - _startTime) >= HoldTimeThreshold)
        {
            CurrentState = GestureState.Holding;
        }

        return CurrentState;
    }

    public GestureState PointerReleased(float x, float y, DateTime time)
    {
        if (!_pointerDown) return GestureState.None;
        _pointerDown = false;

        // One last check before release
        PointerMoved(x, y, time);
        var finalState = CurrentState;
        CurrentState = GestureState.None;

        return finalState;
    }

    public bool CheckHold(DateTime currentTime)
    {
        if (_pointerDown && CurrentState == GestureState.None && (currentTime - _startTime) >= HoldTimeThreshold)
        {
            CurrentState = GestureState.Holding;
            return true;
        }
        return false;
    }
}
