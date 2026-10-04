// ==============================================================================
// Copyright (c) 2026 kaliteConfig
// All rights reserved.
// ==============================================================================
using System;
using System.Collections.Generic;
using System.Linq;
using NvAPIWrapper;
using NvAPIWrapper.Display;
using NvAPIWrapper.Native;
using NvAPIWrapper.Native.Exceptions;
using kaliteConfig.Models;

namespace kaliteConfig.Services
{
    public static class NvidiaDisplayService
    {
        private static readonly object _gate = new();
        private static bool _initialized;

        public static string UnavailableReason { get; private set; } = string.Empty;

        private static bool EnsureInitialized()
        {
            lock (_gate)
            {
                if (_initialized) return true;
                try
                {
                    NVIDIA.Initialize();
                    _initialized = true;
                    return true;
                }
                catch (Exception ex)
                {
                    UnavailableReason = ex.Message;
                    return false;
                }
            }
        }

        public static IReadOnlyList<NvidiaDisplayProfile> Enumerate()
        {
            if (!EnsureInitialized()) return Array.Empty<NvidiaDisplayProfile>();

            var profiles = new List<NvidiaDisplayProfile>();

            // The driver knows a display only by id, but the monitor's own controls are
            // addressed by a rectangle of screen, so the OS topology is read too and the
            // two lists are matched by device name.
            List<DisplayInfo>? osDisplays = null;
            try { osDisplays = DisplayEnumerationService.Enumerate(); }
            catch (Exception) { }

            lock (_gate)
            {
                try
                {
                    var displays = Display.GetDisplays();
                    IReadOnlyList<PathInfo>? paths = null;
                    try { paths = PathInfo.GetDisplaysConfig(); } catch { }

                    foreach (var d in displays)
                    {
                        var profile = new NvidiaDisplayProfile
                        {
                            DeviceName = d.Name,
                            MonitorName = "Physical Monitor", // We would cross-reference WMI for true name
                            DisplayId = (uint)d.Handle.MemoryAddress.ToInt64(),
                            IsNvidiaControlled = d.PhysicalGPUs.Any()
                        };

                        var os = osDisplays?.FirstOrDefault(x =>
                            x.DeviceName.Equals(profile.DeviceName, StringComparison.OrdinalIgnoreCase));
                        if (os != null)
                        {
                            profile.PositionX = os.PositionX;
                            profile.PositionY = os.PositionY;
                            profile.PanelWidth = os.CurrentWidth;
                            profile.PanelHeight = os.CurrentHeight;
                            if (!string.IsNullOrWhiteSpace(os.FriendlyName))
                                profile.MonitorName = os.FriendlyName;
                            if (!string.IsNullOrWhiteSpace(os.ConnectionTypeDisplay))
                                profile.ConnectionLabel = os.ConnectionTypeDisplay;
                        }

                        if (profile.IsNvidiaControlled)
                        {
                            try
                            {
                                var dvc = d.DigitalVibranceControl;
                                profile.SupportsDigitalVibrance = true;
                                profile.DvcDefault = dvc.DefaultLevel;
                                profile.DvcMinimum = dvc.MinimumLevel;
                                profile.DvcMaximum = dvc.MaximumLevel;
                                profile.DigitalVibrance = dvc.CurrentLevel;
                            }
                            catch (Exception) { profile.SupportsDigitalVibrance = false; }

                            try
                            {
                                var hue = d.HUEControl;
                                profile.SupportsHue = true;
                                profile.Hue = hue.CurrentAngle;
                            }
                            catch (Exception) { profile.SupportsHue = false; }

                            ReadColour(profile);
                        }

                        if (paths != null && profile.IsNvidiaControlled)
                        {
                            var target = paths.SelectMany(p => p.TargetsInfo).FirstOrDefault(t => t.DisplayDevice.DisplayId == d.DisplayDevice.DisplayId);
                            if (target != null)
                            {
                                var mapped = NvidiaScalingMap.FromDriver(target.Scaling);
                                profile.SupportsScaling = true;
                                profile.ScalingLocation = mapped.Location;
                                profile.ScalingMode = mapped.Mode;
                            }
                            else profile.SupportsScaling = false;
                        }
                        else profile.SupportsScaling = false;
                        
                        profiles.Add(profile);
                    }
                }
                catch (Exception ex) { UnavailableReason = ex.Message; }
            }
            return profiles;
        }

        /// <summary>
        /// Reads brightness, contrast, gamma and the per-channel gains, and sets the
        /// capability flags from whatever actually answered.
        ///
        /// Two independent routes exist and they are not interchangeable. The driver's
        /// own desktop-colour ramp is the one behind "Adjust desktop color settings" in
        /// the NVIDIA Control Panel, and the monitor's DDC/CI controls are a different
        /// piece of hardware entirely. The driver route is preferred and wins wherever
        /// it answers; the monitor route fills in only what the driver does not expose.
        /// Without this the capability flags were never set, which left every colour
        /// slider bound to <c>Supports…</c> permanently disabled and nothing in Apply
        /// ever wrote brightness or contrast at all.
        /// </summary>
        private static void ReadColour(NvidiaDisplayProfile profile)
        {
            if (NvidiaDesktopColorService.TryRead(profile.DisplayId, out var colour))
            {
                profile.SupportsBrightness = true;
                profile.SupportsContrast = true;
                profile.SupportsGamma = true;
                profile.ColourIsDriverSide = true;
                profile.Brightness = NvidiaDesktopColorService.ToFraction(colour.Brightness);
                profile.Contrast = NvidiaDesktopColorService.ToFraction(colour.Contrast);
                profile.Gamma = NvidiaDesktopColorService.GammaToMultiplier(colour.Gamma);
            }

            if (profile.PanelWidth <= 0 || profile.PanelHeight <= 0)
            {
                return;
            }

            MonitorControlService.Read(
                profile.PositionX, profile.PositionY, profile.PanelWidth, profile.PanelHeight,
                out var brightness, out var contrast, out var gain, out var gainFeature,
                out bool reachable, null);

            if (!reachable)
            {
                // Different from "the monitor has no such control": nothing answered at
                // all, which the panel reports rather than pretending the monitor said no.
                profile.MonitorControlsUnreachable = true;
                return;
            }

            if (!profile.ColourIsDriverSide)
            {
                profile.SupportsBrightness = brightness.Supported;
                profile.SupportsContrast = contrast.Supported;
                // An implemented-but-unreadable control still gets its placeholder, so the
                // slider is usable and only counts as a change once the user moves it.
                profile.Brightness = brightness.ValueKnown ? brightness.Fraction : 0.5;
                profile.Contrast = contrast.ValueKnown ? contrast.Fraction : 0.5;
            }

            profile.SupportsColorGain = gainFeature.Supported;
            profile.ColorGainIsUnread = gainFeature.Supported && !gainFeature.ValueKnown;
            if (gainFeature.Supported) profile.ColorGain = (gain.Red, gain.Green, gain.Blue);
        }

        public static NvidiaDisplayApplyResult Apply(NvidiaDisplayProfile profile)
        {
            var res = new NvidiaDisplayApplyResult();
            if (!EnsureInitialized())
            {
                res.Failed.Add("NVAPI Not Initialized");
                return res;
            }

            lock (_gate)
            {
                var disp = Display.GetDisplays().FirstOrDefault(d => d.Name == profile.DeviceName);
                if (disp == null)
                {
                    res.Failed.Add("Display Not Found");
                    return res;
                }

                ApplyColour(profile, res);

                if (profile.SupportsDigitalVibrance)
                {
                    try
                    {
                        DisplayApi.SetDVCLevelEx(disp.Handle, (int)profile.DigitalVibrance);
                        if (disp.DigitalVibranceControl.CurrentLevel == (int)profile.DigitalVibrance)
                            res.Applied.Add("DigitalVibrance");
                        else
                            res.Failed.Add("DigitalVibrance (silent reject — the driver accepted the call but did not change the level)");
                    }
                    catch (Exception ex)
                    {
                        // The driver's own wording is the only useful thing here: it names
                        // the status (invalid parameter, not supported, ...) that the bare
                        // catch this replaces used to throw away.
                        res.Failed.Add($"DigitalVibrance — {ex.Message}");
                    }
                }

                if (profile.SupportsHue)
                {
                    try
                    {
                        DisplayApi.SetHUEAngle(disp.Handle, (int)profile.Hue);
                        if (disp.HUEControl.CurrentAngle == (int)profile.Hue)
                            res.Applied.Add("Hue");
                        else
                            res.Failed.Add("Hue (silent reject — the driver accepted the call but did not change the angle)");
                    }
                    catch (Exception ex)
                    {
                        res.Failed.Add($"Hue — {ex.Message}");
                    }
                }

                if (profile.SupportsScaling && NvidiaScalingMap.TryToDriver(profile.ScalingLocation, profile.ScalingMode, out var nvScaling))
                {
                    try
                    {
                        ApplyScaling(disp, nvScaling, res);
                    }
                    catch (Exception ex)
                    {
                        res.Failed.Add($"Scaling — {ex.Message}");
                    }
                }
            }

            return res;
        }

        /// <summary>
        /// Writes brightness, contrast, gamma and the channel gains back to whichever
        /// route <see cref="NvidiaDisplayProfile.ColourIsDriverSide"/> says this panel's
        /// values came from.
        /// </summary>
        private static void ApplyColour(NvidiaDisplayProfile profile, NvidiaDisplayApplyResult res)
        {
            bool driverSide = profile.ColourIsDriverSide && profile.IsNvidiaControlled;

            if (driverSide && (profile.SupportsBrightness || profile.SupportsContrast || profile.SupportsGamma))
            {
                var desired = new NvidiaDesktopColorService.Settings(
                    NvidiaDesktopColorService.ToSetting(profile.Brightness),
                    NvidiaDesktopColorService.ToSetting(profile.Contrast),
                    NvidiaDesktopColorService.GammaToSetting(profile.Gamma));

                // Only push when it actually differs: the ramp write is a visible change
                // to the whole desktop, so it must not fire because the user nudged the
                // scaling dropdown.
                bool readBack = NvidiaDesktopColorService.TryRead(profile.DisplayId, out var live, null);
                bool changed = !readBack
                    || Math.Abs(live.Brightness - desired.Brightness) > 0.05f
                    || Math.Abs(live.Contrast - desired.Contrast) > 0.05f
                    || Math.Abs(live.Gamma - desired.Gamma) > 0.05f;

                if (changed)
                {
                    if (NvidiaDesktopColorService.TryWrite(profile.DisplayId, desired))
                        res.Applied.Add("Brightness, Contrast, Gamma");
                    else
                        res.Failed.Add("Brightness, Contrast, Gamma — the driver refused the colour ramp");
                }
            }

            if (driverSide && !profile.SupportsColorGain) return;
            if (profile.PanelWidth <= 0 || profile.PanelHeight <= 0)
            {
                if (!driverSide)
                    res.Failed.Add("Brightness, Contrast — this display has no desktop geometry, " +
                                   "so the monitor's own controls could not be reached");
                return;
            }

            double? brightness = !driverSide && profile.SupportsBrightness ? profile.Brightness : null;
            double? contrast = !driverSide && profile.SupportsContrast ? profile.Contrast : null;
            MonitorControlService.RgbGain? gain = profile.SupportsColorGain && !profile.ColorGainIsUnread
                ? new MonitorControlService.RgbGain(profile.ColorGain.Red, profile.ColorGain.Green, profile.ColorGain.Blue)
                : null;

            if (brightness is null && contrast is null && gain is null) return;

            if (MonitorControlService.Write(
                    profile.PositionX, profile.PositionY, profile.PanelWidth, profile.PanelHeight,
                    brightness, contrast, gain))
            {
                var names = new List<string>();
                if (brightness.HasValue) names.Add("Brightness");
                if (contrast.HasValue) names.Add("Contrast");
                if (gain.HasValue) names.Add("Channel gains");
                res.Applied.Add(string.Join(", ", names));
            }
            else
            {
                res.Failed.Add("Brightness, Contrast, Channel gains — the monitor refused the DDC/CI write");
            }
        }

        /// <summary>
        /// Applies one path's scan-out scaling.
        ///
        /// The mutation is written back into the target array rather than only onto a
        /// value pulled out of it. <c>SelectMany</c> hands back the element, but whether
        /// that is the same object the array holds depends on the wrapper's element type —
        /// and when it is a copy, the write is discarded silently and the panel reports a
        /// scaling change that never happened.
        /// </summary>
        private static void ApplyScaling(Display disp, NvAPIWrapper.Native.Display.Scaling nvScaling,
            NvidiaDisplayApplyResult res)
        {
            var paths = PathInfo.GetDisplaysConfig().ToArray();

            int pathIndex = Array.FindIndex(paths, p =>
                p.TargetsInfo.Any(t => t.DisplayDevice.DisplayId == disp.DisplayDevice.DisplayId));
            if (pathIndex < 0) return;

            var targets = paths[pathIndex].TargetsInfo;
            int targetIndex = Array.FindIndex(targets, t =>
                t.DisplayDevice.DisplayId == disp.DisplayDevice.DisplayId);
            if (targetIndex < 0) return;

            if (targets[targetIndex].Scaling == nvScaling)
            {
                // Already what the user asked for; nothing to write and nothing to report.
                return;
            }

            var target = targets[targetIndex];
            target.Scaling = nvScaling;
            targets[targetIndex] = target;

            // Fully qualified: the bare name is ambiguous between the NvAPI wrapper's
            // namespace and the Display type it forwards to.
            PathInfo.SetDisplaysConfig(paths, NvAPIWrapper.Native.Display.DisplayConfigFlags.None);

            var newPaths = PathInfo.GetDisplaysConfig();
            var newTarget = newPaths.SelectMany(p => p.TargetsInfo)
                .FirstOrDefault(t => t.DisplayDevice.DisplayId == disp.DisplayDevice.DisplayId);
            if (newTarget != null && newTarget.Scaling == nvScaling)
                res.Applied.Add("Scaling");
            else
                res.Failed.Add($"Scaling — the driver kept {newTarget?.Scaling.ToString() ?? "nothing"} " +
                               $"instead of {nvScaling}");
        }

        public static NvidiaDisplayApplyResult Restore(NvidiaDisplayProfile profile)
        {
            return Apply(profile);
        }
    }
}
