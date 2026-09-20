using System;
using kaliteConfig.Services;

// Unit tests for the UI-free capture rules in SnipRegionLogic.
internal static class RegionLogicTests
{
    public static void Run(Action<bool, string> check)
    {
        // Normalize in all four drag directions + zero-size.
        var r = SnipRegionLogic.Normalize(10, 10, 30, 20);
        check(r.X == 10 && r.Y == 10 && r.W == 20 && r.H == 10, "region normalize ↘");
        r = SnipRegionLogic.Normalize(30, 20, 10, 10);
        check(r.X == 10 && r.Y == 10 && r.W == 20 && r.H == 10, "region normalize ↖ same rect");
        r = SnipRegionLogic.Normalize(30, 10, 10, 20);
        check(r.X == 10 && r.Y == 10 && r.W == 20 && r.H == 10, "region normalize ↙");
        r = SnipRegionLogic.Normalize(10, 20, 30, 10);
        check(r.X == 10 && r.Y == 10 && r.W == 20 && r.H == 10, "region normalize ↗");
        r = SnipRegionLogic.Normalize(5, 5, 5, 5);
        check(r.W == 0 && r.H == 0, "region normalize zero-size drag");
        r = SnipRegionLogic.Normalize(-30, -10, -10, -40);
        check(r.X == -30 && r.Y == -40 && r.W == 20 && r.H == 30, "region normalize negative coords");

        // Clamp to image.
        r = SnipRegionLogic.ClampToImage(new SnipRect(10, 10, 20, 20), 100, 100);
        check(r.X == 10 && r.W == 20, "region clamp inside unchanged");
        r = SnipRegionLogic.ClampToImage(new SnipRect(-10, -5, 200, 200), 100, 80);
        check(r.X == 0 && r.Y == 0 && r.W == 100 && r.H == 80, "region clamp overflowing");
        r = SnipRegionLogic.ClampToImage(new SnipRect(0, 0, 0, 0), 100, 100);
        check(r.W == 0 && r.H == 0, "region clamp zero stays zero");

        // Click threshold (~3 px).
        check(SnipRegionLogic.IsClick(0, 0, 2.9, 0), "region 2.9px is a click");
        check(!SnipRegionLogic.IsClick(0, 0, 3.0, 0), "region 3.0px is a drag");
        check(!SnipRegionLogic.IsClick(0, 0, 0, 3.1), "region 3.1px vertical is a drag");

        // DIP/physical at 100/125/150/200%, incl. negative coords.
        check(SnipRegionLogic.DipsToPhysical(100, 1.0) == 100, "region dips 100%@1.0");
        check(SnipRegionLogic.DipsToPhysical(100, 1.25) == 125, "region dips 100@1.25");
        check(SnipRegionLogic.DipsToPhysical(100, 1.5) == 150, "region dips 100@1.5");
        check(SnipRegionLogic.DipsToPhysical(100, 2.0) == 200, "region dips 100@2.0");
        check(SnipRegionLogic.DipsToPhysical(-50, 1.25) == -62.5, "region dips negative coord");
        check(SnipRegionLogic.PhysicalToDips(150, 1.5) == 100, "region px back to dips");
        check(SnipRegionLogic.PhysicalToDips(50, 0) == 50, "region zero-scale guard");

        // Modifier policy.
        check(SnipRegionLogic.RegionIgnoresModifiers(), "region ignores all modifiers");
        check(SnipRegionLogic.ShouldSquareShape(true, true, false), "policy square: shape+live shift");
        check(!SnipRegionLogic.ShouldSquareShape(true, true, true), "policy no square while shift preheld");
        check(!SnipRegionLogic.ShouldSquareShape(true, false, false), "policy no square without shift");
        check(!SnipRegionLogic.ShouldSquareShape(false, true, false), "policy no square for non-shape tool");

        // Preheld tracker.
        var t = new SnipRegionLogic.ModifierHoldTracker();
        t.CapturePreheld(true, false, true);
        check(t.ShiftPreheld && t.AltPreheld && !t.CtrlPreheld, "policy tracker captures preheld");
        t.ObserveLive(false, false, true);
        check(!t.ShiftPreheld && t.AltPreheld, "policy tracker releases observed-up keys");
        t.ObserveLive(false, false, false);
        check(!t.ShiftPreheld && !t.AltPreheld, "policy tracker clears all");

        // Square constraint.
        r = SnipRegionLogic.ApplySquare(0, 0, 10, 4);
        check(r.W == 10 && r.H == 10, "region square wide drag");
        r = SnipRegionLogic.ApplySquare(0, 0, 4, 10);
        check(r.W == 10 && r.H == 10, "region square tall drag");
        r = SnipRegionLogic.ApplySquare(0, 0, -6, -3);
        check(r.X == -6 && r.Y == -6 && r.W == 6 && r.H == 6, "region square negative dir");

        // Hit test (the overlay delegates to this).
        check(SnipRegionLogic.HitTestSelection(0, 0, 0, 0, 5, 5, 12) == SnipHit.NewSelection, "region hit empty rect");
        check(SnipRegionLogic.HitTestSelection(10, 10, 80, 60, 10, 10, 12) == SnipHit.TopLeft, "region hit corner");
        check(SnipRegionLogic.HitTestSelection(10, 10, 80, 60, 90, 70, 12) == SnipHit.BottomRight, "region hit far corner");
        check(SnipRegionLogic.HitTestSelection(10, 10, 80, 60, 50, 40, 12) == SnipHit.RootPan, "region hit interior pans");
        check(SnipRegionLogic.HitTestSelection(10, 10, 80, 60, 200, 200, 12) == SnipHit.NewSelection, "region hit outside restarts");
        check(SnipRegionLogic.HitTestSelection(10, 10, 80, 60, 50, 10, 12) == SnipHit.TopCenter, "region hit edge midpoint");

        // Filename template.
        check(SnipRegionLogic.BuildSnipFileName(new DateTime(2026, 9, 20, 12, 0, 0, 123), ".png") == "Snip_20260920_120000_123.png", "region filename template");
        check(SnipRegionLogic.BuildSnipFileName(new DateTime(2026, 1, 2, 3, 4, 5, 6), "jpg") == "Snip_20260102_030405_006.jpg", "region filename ext normalized");

        // DIB bytes: 1x2 image, distinct rows to prove bottom-up order.
        byte[] px = { 10, 20, 30, 255, 40, 50, 60, 255 }; // top row, bottom row
        var dib = SnipRegionLogic.BuildDib32(px, 1, 2);
        check(dib.Length == 40 + 8, "region dib total size");
        check(dib[0] == 40 && dib[4] == 1 && dib[8] == 2 && dib[12] == 1 && dib[14] == 32, "region dib header fields");
        check(dib[40] == 40 && dib[41] == 50 && dib[42] == 60, "region dib bottom row first");
        check(dib[44] == 10 && dib[45] == 20 && dib[46] == 30, "region dib top row last");
        bool threw = false;
        try { SnipRegionLogic.BuildDib32(new byte[4], 2, 2); } catch { threw = true; }
        check(threw, "region dib rejects short buffer");

        // Uniform detection.
        check(SnipGalleryQuery.IsUniformImage(new byte[] { 1, 2, 3, 255, 1, 2, 3, 255 }), "region uniform flat image");
        check(!SnipGalleryQuery.IsUniformImage(new byte[] { 1, 2, 3, 255, 9, 9, 9, 255 }), "region non-uniform detected");
        check(SnipGalleryQuery.IsUniformImage(Array.Empty<byte>()), "region uniform empty guard");
    }
}
