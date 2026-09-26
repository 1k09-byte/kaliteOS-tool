// ==============================================================================
// Copyright (c) 2026 kaliteConfig
// All rights reserved.
//
// This software and associated documentation files are proprietary.
// You may not use, copy, reproduce, modify, merge, publish, distribute, sublicense,
// reverse-engineer, or sell copies of the software in any form, in whole or in part,
// without the express written permission of the copyright holder.
// ==============================================================================
using System;
using System.Collections.Generic;
using Microsoft.Graphics.Canvas;
using Windows.Foundation;

namespace kaliteConfig.Models;

public abstract class SnipObject
{
    public Windows.UI.Color Color { get; set; } = Microsoft.UI.Colors.Red;
    public float StrokeThickness { get; set; } = 4f;
    public bool IsSelected { get; set; }

    public abstract void Draw(CanvasDrawingSession ds, CanvasBitmap? background = null);
    public abstract bool HitTest(Point pt);
    public abstract void UpdateBounds(Point start, Point current);
    public abstract void MoveBy(double dx, double dy);
    public abstract Rect GetBounds();
}

public class SnipArrow : SnipObject
{
    public Point Start { get; set; }
    public Point End { get; set; }

    public override void Draw(CanvasDrawingSession ds, CanvasBitmap? background = null)
    {
        ds.DrawLine((float)Start.X, (float)Start.Y, (float)End.X, (float)End.Y, Color, StrokeThickness);

        // Calculate arrowhead
        double angle = Math.Atan2(End.Y - Start.Y, End.X - Start.X);
        double arrowLen = 15;
        double arrowAngle = Math.PI / 6;

        float p1X = (float)(End.X - arrowLen * Math.Cos(angle - arrowAngle));
        float p1Y = (float)(End.Y - arrowLen * Math.Sin(angle - arrowAngle));
        float p2X = (float)(End.X - arrowLen * Math.Cos(angle + arrowAngle));
        float p2Y = (float)(End.Y - arrowLen * Math.Sin(angle + arrowAngle));

        ds.DrawLine((float)End.X, (float)End.Y, p1X, p1Y, Color, StrokeThickness);
        ds.DrawLine((float)End.X, (float)End.Y, p2X, p2Y, Color, StrokeThickness);
    }

    public override bool HitTest(Point pt)
    {
        // Simple distance to line hit test
        double l2 = Math.Pow(Start.X - End.X, 2) + Math.Pow(Start.Y - End.Y, 2);
        if (l2 == 0) return Math.Sqrt(Math.Pow(pt.X - Start.X, 2) + Math.Pow(pt.Y - Start.Y, 2)) < StrokeThickness * 2;
        
        double t = Math.Max(0, Math.Min(1, ((pt.X - Start.X) * (End.X - Start.X) + (pt.Y - Start.Y) * (End.Y - Start.Y)) / l2));
        double projX = Start.X + t * (End.X - Start.X);
        double projY = Start.Y + t * (End.Y - Start.Y);
        
        return Math.Sqrt(Math.Pow(pt.X - projX, 2) + Math.Pow(pt.Y - projY, 2)) < StrokeThickness * 2;
    }

    public override void UpdateBounds(Point start, Point current)
    {
        Start = start;
        End = current;
    }

    public override void MoveBy(double dx, double dy)
    {
        Start = new Point(Start.X + dx, Start.Y + dy);
        End = new Point(End.X + dx, End.Y + dy);
    }

    public override Rect GetBounds() => new Rect(
        Math.Min(Start.X, End.X), Math.Min(Start.Y, End.Y),
        Math.Abs(End.X - Start.X), Math.Abs(End.Y - Start.Y));
}

public class SnipRectangle : SnipObject
{
    public Rect Bounds { get; set; }

    public override void Draw(CanvasDrawingSession ds, CanvasBitmap? background = null)
    {
        ds.DrawRectangle((float)Bounds.X, (float)Bounds.Y, (float)Bounds.Width, (float)Bounds.Height, Color, StrokeThickness);
    }

    public override bool HitTest(Point pt)
    {
        bool outer = pt.X >= Bounds.X - StrokeThickness && pt.X <= Bounds.X + Bounds.Width + StrokeThickness &&
                     pt.Y >= Bounds.Y - StrokeThickness && pt.Y <= Bounds.Y + Bounds.Height + StrokeThickness;
        bool inner = pt.X >= Bounds.X + StrokeThickness && pt.X <= Bounds.X + Bounds.Width - StrokeThickness &&
                     pt.Y >= Bounds.Y + StrokeThickness && pt.Y <= Bounds.Y + Bounds.Height - StrokeThickness;
        return outer && !inner;
    }

    public override void UpdateBounds(Point start, Point current)
    {
        Bounds = new Rect(
            Math.Min(start.X, current.X),
            Math.Min(start.Y, current.Y),
            Math.Abs(start.X - current.X),
            Math.Abs(start.Y - current.Y)
        );
    }

    public override void MoveBy(double dx, double dy) =>
        Bounds = new Rect(Bounds.X + dx, Bounds.Y + dy, Bounds.Width, Bounds.Height);

    public override Rect GetBounds() => Bounds;
}

public class SnipRedactionBox : SnipObject
{
    public Rect Bounds { get; set; }
    public bool PixelateMode { get; set; } = false;

    public override void Draw(CanvasDrawingSession ds, CanvasBitmap? background = null)
    {
        if (background == null) return;
        
        // We want to blur or pixelate ONLY inside the Bounds region
        if (Bounds.Width <= 0 || Bounds.Height <= 0) return;

        var crop = new Microsoft.Graphics.Canvas.Effects.CropEffect 
        { 
            Source = background, 
            SourceRectangle = Bounds 
        };

        if (PixelateMode)
        {
            var scaleDown = new Microsoft.Graphics.Canvas.Effects.ScaleEffect { Source = crop, Scale = new System.Numerics.Vector2(0.1f) };
            var scaleUp = new Microsoft.Graphics.Canvas.Effects.ScaleEffect { Source = scaleDown, Scale = new System.Numerics.Vector2(10f), InterpolationMode = Microsoft.Graphics.Canvas.CanvasImageInterpolation.NearestNeighbor };
            ds.DrawImage(scaleUp, Bounds, Bounds);
        }
        else
        {
            var blur = new Microsoft.Graphics.Canvas.Effects.GaussianBlurEffect
            {
                Source = crop,
                BlurAmount = 12f
            };
            ds.DrawImage(blur, Bounds, Bounds);
        }
    }

    public override bool HitTest(Point pt)
    {
        return pt.X >= Bounds.X && pt.X <= Bounds.X + Bounds.Width && 
               pt.Y >= Bounds.Y && pt.Y <= Bounds.Y + Bounds.Height;
    }

    public override void UpdateBounds(Point start, Point current)
    {
        Bounds = new Rect(
            Math.Min(start.X, current.X),
            Math.Min(start.Y, current.Y),
            Math.Abs(start.X - current.X),
            Math.Abs(start.Y - current.Y)
        );
    }

    public override void MoveBy(double dx, double dy) =>
        Bounds = new Rect(Bounds.X + dx, Bounds.Y + dy, Bounds.Width, Bounds.Height);

    public override Rect GetBounds() => Bounds;
}

public class SnipLine : SnipObject
{
    public Point Start { get; set; }
    public Point End { get; set; }

    public override void Draw(CanvasDrawingSession ds, CanvasBitmap? background = null)
        => ds.DrawLine((float)Start.X, (float)Start.Y, (float)End.X, (float)End.Y, Color, StrokeThickness);

    public override bool HitTest(Point pt) => new SnipArrow { Start = Start, End = End, StrokeThickness = StrokeThickness }.HitTest(pt);

    public override void UpdateBounds(Point start, Point current) { Start = start; End = current; }

    public override void MoveBy(double dx, double dy)
    {
        Start = new Point(Start.X + dx, Start.Y + dy);
        End = new Point(End.X + dx, End.Y + dy);
    }

    public override Rect GetBounds() => new Rect(
        Math.Min(Start.X, End.X), Math.Min(Start.Y, End.Y),
        Math.Abs(End.X - Start.X), Math.Abs(End.Y - Start.Y));
}

public class SnipEllipse : SnipObject
{
    public Rect Bounds { get; set; }
    public bool Fill { get; set; }

    public override void Draw(CanvasDrawingSession ds, CanvasBitmap? background = null)
    {
        if (Bounds.Width <= 0 || Bounds.Height <= 0) return;
        float cx = (float)(Bounds.X + Bounds.Width / 2), cy = (float)(Bounds.Y + Bounds.Height / 2);
        float rx = (float)Bounds.Width / 2, ry = (float)Bounds.Height / 2;
        if (Fill) ds.FillEllipse(cx, cy, rx, ry, Color);
        else ds.DrawEllipse(cx, cy, rx, ry, Color, StrokeThickness);
    }

    public override bool HitTest(Point pt)
    {
        double cx = Bounds.X + Bounds.Width / 2, cy = Bounds.Y + Bounds.Height / 2;
        double nx = (pt.X - cx) / Math.Max(1, Bounds.Width / 2), ny = (pt.Y - cy) / Math.Max(1, Bounds.Height / 2);
        double d = nx * nx + ny * ny;
        return Fill ? d <= 1 : d <= 1.05 && d >= 0.8;
    }

    public override void UpdateBounds(Point start, Point current)
    {
        Bounds = new Rect(
            Math.Min(start.X, current.X), Math.Min(start.Y, current.Y),
            Math.Abs(start.X - current.X), Math.Abs(start.Y - current.Y));
    }

    public override void MoveBy(double dx, double dy) =>
        Bounds = new Rect(Bounds.X + dx, Bounds.Y + dy, Bounds.Width, Bounds.Height);

    public override Rect GetBounds() => Bounds;
}

/// <summary>Translucent wide ink stroke used by the highlighter tool.</summary>
public class SnipHighlight : SnipInk
{
    public SnipHighlight()
    {
        var c = Color; Color = Windows.UI.Color.FromArgb(90, c.R, c.G, c.B);
    }

    public override void Draw(CanvasDrawingSession ds, CanvasBitmap? background = null)
    {
        if (Points.Count < 2) return;
        var pds = new Microsoft.Graphics.Canvas.Geometry.CanvasPathBuilder(ds.Device);
        pds.BeginFigure((float)Points[0].X, (float)Points[0].Y);
        for (int i = 1; i < Points.Count; i++) pds.AddLine((float)Points[i].X, (float)Points[i].Y);
        pds.EndFigure(Microsoft.Graphics.Canvas.Geometry.CanvasFigureLoop.Open);
        using var geom = Microsoft.Graphics.Canvas.Geometry.CanvasGeometry.CreatePath(pds);
        ds.DrawGeometry(geom, Color, Math.Max(StrokeThickness * 4, 16));
    }
}

/// <summary>Dims everything outside an ellipse so the marked area stands out.</summary>
public class SnipSpotlight : SnipObject
{
    public Rect Bounds { get; set; }
    public int ScreenWidth { get; set; }
    public int ScreenHeight { get; set; }

    private static readonly Windows.UI.Color Dim = Windows.UI.Color.FromArgb(150, 0, 0, 0);

    public override void Draw(CanvasDrawingSession ds, CanvasBitmap? background = null)
    {
        if (Bounds.Width <= 0 || Bounds.Height <= 0 || ScreenWidth <= 0) return;
        // Four dim rectangles around the ellipse (cheap, correct silhouette).
        ds.FillRectangle(0, 0, ScreenWidth, (float)Bounds.Y, Dim);
        ds.FillRectangle(0, (float)(Bounds.Y + Bounds.Height), ScreenWidth, ScreenHeight - (float)(Bounds.Y + Bounds.Height), Dim);
        ds.FillRectangle(0, (float)Bounds.Y, (float)Bounds.X, (float)Bounds.Height, Dim);
        ds.FillRectangle((float)(Bounds.X + Bounds.Width), (float)Bounds.Y, ScreenWidth - (float)(Bounds.X + Bounds.Width), (float)Bounds.Height, Dim);
        ds.DrawEllipse((float)(Bounds.X + Bounds.Width / 2), (float)(Bounds.Y + Bounds.Height / 2), (float)Bounds.Width / 2, (float)Bounds.Height / 2, Color, StrokeThickness);
    }

    public override bool HitTest(Point pt) => Bounds.X <= pt.X && pt.X <= Bounds.X + Bounds.Width && Bounds.Y <= pt.Y && pt.Y <= Bounds.Y + Bounds.Height;

    public override void UpdateBounds(Point start, Point current)
    {
        Bounds = new Rect(
            Math.Min(start.X, current.X), Math.Min(start.Y, current.Y),
            Math.Abs(start.X - current.X), Math.Abs(start.Y - current.Y));
    }

    public override void MoveBy(double dx, double dy) =>
        Bounds = new Rect(Bounds.X + dx, Bounds.Y + dy, Bounds.Width, Bounds.Height);

    public override Rect GetBounds() => Bounds;
}

/// <summary>Free-floating text annotation.</summary>
public class SnipText : SnipObject
{
    public string TextValue { get; set; } = "";
    public Point Position { get; set; }
    public float FontSize { get; set; } = 22f;

    public override void Draw(CanvasDrawingSession ds, CanvasBitmap? background = null)
    {
        if (string.IsNullOrEmpty(TextValue)) return;
        using var fmt = new Microsoft.Graphics.Canvas.Text.CanvasTextFormat { FontSize = FontSize };
        ds.DrawText(TextValue, (float)Position.X, (float)Position.Y, Color, fmt);
    }

    public override bool HitTest(Point pt) =>
        pt.X >= Position.X && pt.X <= Position.X + TextValue.Length * FontSize * 0.6 &&
        pt.Y >= Position.Y && pt.Y <= Position.Y + FontSize * 1.4;

    public override void UpdateBounds(Point start, Point current) => Position = start;

    public override void MoveBy(double dx, double dy) => Position = new Point(Position.X + dx, Position.Y + dy);

    public override Rect GetBounds() => new Rect(Position.X, Position.Y,
        Math.Max(8, TextValue.Length * FontSize * 0.6), FontSize * 1.4);
}

/// <summary>Numbered step badge: filled circle with the next sequence number.</summary>
public class SnipNumber : SnipObject
{
    public int Number { get; set; } = 1;
    public Point Center { get; set; }
    public float Radius => Math.Max(12f, StrokeThickness * 4);

    public override void Draw(CanvasDrawingSession ds, CanvasBitmap? background = null)
    {
        ds.FillCircle((float)Center.X, (float)Center.Y, Radius, Color);
        using var fmt = new Microsoft.Graphics.Canvas.Text.CanvasTextFormat
        {
            FontSize = Radius * 1.2f,
            HorizontalAlignment = Microsoft.Graphics.Canvas.Text.CanvasHorizontalAlignment.Center,
            VerticalAlignment = Microsoft.Graphics.Canvas.Text.CanvasVerticalAlignment.Center,
        };
        ds.DrawText(Number.ToString(), (float)Center.X, (float)(Center.Y - Radius / 2), Microsoft.UI.Colors.White, fmt);
    }

    public override bool HitTest(Point pt) =>
        Math.Sqrt(Math.Pow(pt.X - Center.X, 2) + Math.Pow(pt.Y - Center.Y, 2)) <= Radius * 1.5;

    public override void UpdateBounds(Point start, Point current) => Center = start;

    public override void MoveBy(double dx, double dy) => Center = new Point(Center.X + dx, Center.Y + dy);

    public override Rect GetBounds()
    {
        double r = Radius * 1.5;
        return new Rect(Center.X - r, Center.Y - r, r * 2, r * 2);
    }
}

public class SnipInk : SnipObject
{
    public List<Point> Points { get; private set; } = new();

    public override void Draw(CanvasDrawingSession ds, CanvasBitmap? background = null)
    {
        if (Points.Count < 2) return;
        var pds = new Microsoft.Graphics.Canvas.Geometry.CanvasPathBuilder(ds.Device);
        pds.BeginFigure((float)Points[0].X, (float)Points[0].Y);
        for (int i = 1; i < Points.Count; i++)
        {
            pds.AddLine((float)Points[i].X, (float)Points[i].Y);
        }
        pds.EndFigure(Microsoft.Graphics.Canvas.Geometry.CanvasFigureLoop.Open);
        
        var geom = Microsoft.Graphics.Canvas.Geometry.CanvasGeometry.CreatePath(pds);
        ds.DrawGeometry(geom, Color, StrokeThickness);
    }

    public override bool HitTest(Point pt)
    {
        foreach (var p in Points)
        {
            if (Math.Sqrt(Math.Pow(p.X - pt.X, 2) + Math.Pow(p.Y - pt.Y, 2)) < StrokeThickness * 2) return true;
        }
        return false;
    }

    public override void UpdateBounds(Point start, Point current)
    {
        Points.Add(current);
    }

    public override void MoveBy(double dx, double dy)
    {
        for (int i = 0; i < Points.Count; i++)
            Points[i] = new Point(Points[i].X + dx, Points[i].Y + dy);
    }

    public override Rect GetBounds()
    {
        if (Points.Count == 0) return new Rect(0, 0, 0, 0);
        double x0 = Points[0].X, y0 = Points[0].Y, x1 = x0, y1 = y0;
        foreach (var p in Points)
        {
            if (p.X < x0) x0 = p.X; if (p.X > x1) x1 = p.X;
            if (p.Y < y0) y0 = p.Y; if (p.Y > y1) y1 = p.Y;
        }
        return new Rect(x0, y0, Math.Max(1, x1 - x0), Math.Max(1, y1 - y0));
    }
}

/// <summary>Stamped image annotation. Holds decoded BGRA pixels plus a per-device
/// cached bitmap; draws (and exports) like any other retained object.</summary>
public class SnipSticker : SnipObject
{
    public byte[] Bgra { get; set; } = Array.Empty<byte>();
    public int PixelWidth { get; set; }
    public int PixelHeight { get; set; }
    public Rect Bounds { get; set; }

    private CanvasBitmap? _cached;
    private CanvasDevice? _cachedDevice;

    public override void Draw(CanvasDrawingSession ds, CanvasBitmap? background = null)
    {
        if (Bgra.Length < PixelWidth * PixelHeight * 4 || PixelWidth <= 0 || PixelHeight <= 0) return;
        if (Bounds.Width <= 0 || Bounds.Height <= 0) return;
        try
        {
            if (_cached is null || _cachedDevice is null || !ReferenceEquals(_cachedDevice, ds.Device))
            {
                _cached?.Dispose();
                _cached = CanvasBitmap.CreateFromBytes(ds.Device, Bgra, PixelWidth, PixelHeight,
                    Windows.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized);
                _cachedDevice = ds.Device;
            }
            ds.DrawImage(_cached, Bounds,
                new Rect(0, 0, PixelWidth, PixelHeight));
        }
        catch { /* a bad sticker never breaks the whole frame */ }
    }

    public override bool HitTest(Point pt) =>
        pt.X >= Bounds.X - 4 && pt.X <= Bounds.X + Bounds.Width + 4 &&
        pt.Y >= Bounds.Y - 4 && pt.Y <= Bounds.Y + Bounds.Height + 4;

    public override void UpdateBounds(Point start, Point current)
    {
        Bounds = new Rect(
            Math.Min(start.X, current.X),
            Math.Min(start.Y, current.Y),
            Math.Abs(start.X - current.X),
            Math.Abs(start.Y - current.Y));
    }

    public override void MoveBy(double dx, double dy) =>
        Bounds = new Rect(Bounds.X + dx, Bounds.Y + dy, Bounds.Width, Bounds.Height);

    public override Rect GetBounds() => Bounds;
}
