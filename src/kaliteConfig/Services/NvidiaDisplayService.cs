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
                            catch { profile.SupportsDigitalVibrance = false; }

                            try
                            {
                                var hue = d.HUEControl;
                                profile.SupportsHue = true;
                                profile.Hue = hue.CurrentAngle;
                            }
                            catch { profile.SupportsHue = false; }
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

                if (profile.SupportsDigitalVibrance)
                {
                    try
                    {
                        DisplayApi.SetDVCLevelEx(disp.Handle, (int)profile.DigitalVibrance);
                        if (disp.DigitalVibranceControl.CurrentLevel == (int)profile.DigitalVibrance)
                            res.Applied.Add("DigitalVibrance");
                        else
                            res.Failed.Add("DigitalVibrance (Silent Reject)");
                    }
                    catch { res.Failed.Add("DigitalVibrance"); }
                }

                if (profile.SupportsHue)
                {
                    try
                    {
                        DisplayApi.SetHUEAngle(disp.Handle, (int)profile.Hue);
                        if (disp.HUEControl.CurrentAngle == (int)profile.Hue)
                            res.Applied.Add("Hue");
                        else
                            res.Failed.Add("Hue (Silent Reject)");
                    }
                    catch { res.Failed.Add("Hue"); }
                }

                if (profile.SupportsScaling && NvidiaScalingMap.TryToDriver(profile.ScalingLocation, profile.ScalingMode, out var nvScaling))
                {
                    try
                    {
                        var paths = PathInfo.GetDisplaysConfig();
                        var target = paths.SelectMany(p => p.TargetsInfo).FirstOrDefault(t => t.DisplayDevice.DisplayId == disp.DisplayDevice.DisplayId);
                        if (target != null && target.Scaling != nvScaling)
                        {
                            target.Scaling = nvScaling;
                            PathInfo.SetDisplaysConfig(paths, (NvAPIWrapper.Native.Display.DisplayConfigFlags)0);
                            
                            var newPaths = PathInfo.GetDisplaysConfig();
                            var newTarget = newPaths.SelectMany(p => p.TargetsInfo).FirstOrDefault(t => t.DisplayDevice.DisplayId == disp.DisplayDevice.DisplayId);
                            if (newTarget != null && newTarget.Scaling == nvScaling)
                                res.Applied.Add("Scaling");
                            else
                                res.Failed.Add("Scaling (Silent Reject)");
                        }
                        else if (target != null && target.Scaling == nvScaling)
                        {
                            // Already applied
                        }
                    }
                    catch { res.Failed.Add("Scaling"); }
                }
            }

            return res;
        }

        public static NvidiaDisplayApplyResult Restore(NvidiaDisplayProfile profile)
        {
            return Apply(profile);
        }
    }
}
