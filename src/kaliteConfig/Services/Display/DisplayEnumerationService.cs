// ==============================================================================
// Copyright (c) 2026 kaliteConfig
// All rights reserved.
// ==============================================================================
using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using kaliteConfig.Models;
using static kaliteConfig.Native.NativeMethods.Display;

namespace kaliteConfig.Services;

/// <summary>
/// Enumerates connected displays via CCD + WMI, parses raw EDID for identity,
/// and returns <see cref="DisplayInfo"/> objects.
/// <para><b>Technology: A (WMI read-only) + C (CCD)</b></para>
/// </summary>
internal static class DisplayEnumerationService
{
    /// <summary>
    /// Discovers all connected displays with identity and live state.
    /// Never throws; returns empty on failure.
    /// </summary>
    public static List<DisplayInfo> Enumerate()
    {
        var results = new List<DisplayInfo>();
        try
        {
            // ── Step 1: CCD topology (active paths, modes, positions) ──
            int hr = GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out uint pathCount, out uint modeCount);
            if (hr != 0) return results;

            var paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
            var modes = new DISPLAYCONFIG_MODE_INFO[modeCount];

            hr = QueryDisplayConfig(
                QDC_ONLY_ACTIVE_PATHS,
                ref pathCount, paths,
                ref modeCount, modes,
                IntPtr.Zero);
            if (hr != 0) return results;

            // ── Step 2: WMI EDID data keyed by instance name stem ──
            var edidMap = ReadWmiEdidMap();

            // ── Step 3: Determine primary display ──
            string primaryDevName = GetPrimaryDeviceName();

            // ── Step 4: Build DisplayInfo for each active path ──
            foreach (var path in paths.Take((int)pathCount))
            {
                if ((path.Flags & DISPLAYCONFIG_PATH_ACTIVE) == 0) continue;

                // Get GDI device name (\\.\DISPLAY1)
                var srcName = new DISPLAYCONFIG_SOURCE_DEVICE_NAME();
                srcName.Header.Type = DISPLAYCONFIG_DEVICE_INFO_TYPE.GetSourceName;
                srcName.Header.Size = (uint)Marshal.SizeOf<DISPLAYCONFIG_SOURCE_DEVICE_NAME>();
                srcName.Header.AdapterId = path.SourceInfo.AdapterId;
                srcName.Header.Id = path.SourceInfo.Id;
                if (DisplayConfigGetDeviceInfo(ref srcName) != 0) continue;

                // Get friendly name + monitor device path
                var tgtName = new DISPLAYCONFIG_TARGET_DEVICE_NAME();
                tgtName.Header.Type = DISPLAYCONFIG_DEVICE_INFO_TYPE.GetTargetName;
                tgtName.Header.Size = (uint)Marshal.SizeOf<DISPLAYCONFIG_TARGET_DEVICE_NAME>();
                tgtName.Header.AdapterId = path.TargetInfo.AdapterId;
                tgtName.Header.Id = path.TargetInfo.Id;
                _ = DisplayConfigGetDeviceInfo(ref tgtName);

                // Source mode (resolution, position)
                uint srcWidth = 0, srcHeight = 0;
                int posX = 0, posY = 0;
                if (path.SourceInfo.ModeInfoIdx < modeCount)
                {
                    var srcMode = modes[path.SourceInfo.ModeInfoIdx];
                    if (srcMode.InfoType == DISPLAYCONFIG_MODE_INFO_TYPE.Source)
                    {
                        srcWidth = srcMode.Info.SourceMode.Width;
                        srcHeight = srcMode.Info.SourceMode.Height;
                        posX = srcMode.Info.SourceMode.Position.X;
                        posY = srcMode.Info.SourceMode.Position.Y;
                    }
                }

                // Target mode (refresh rate)
                double refreshRate = 0;
                if (path.TargetInfo.ModeInfoIdx < modeCount)
                {
                    var tgtMode = modes[path.TargetInfo.ModeInfoIdx];
                    if (tgtMode.InfoType == DISPLAYCONFIG_MODE_INFO_TYPE.Target)
                    {
                        var sig = tgtMode.Info.TargetMode.TargetVideoSignalInfo;
                        if (sig.VSyncFreq.Denominator > 0)
                            refreshRate = (double)sig.VSyncFreq.Numerator / sig.VSyncFreq.Denominator;
                    }
                }

                // Rotation
                int rotDeg = path.TargetInfo.Rotation switch
                {
                    DISPLAYCONFIG_ROTATION.Rotate90 => 90,
                    DISPLAYCONFIG_ROTATION.Rotate180 => 180,
                    DISPLAYCONFIG_ROTATION.Rotate270 => 270,
                    _ => 0,
                };

                // Match WMI EDID by monitor device path
                var edid = MatchEdid(edidMap, tgtName.MonitorDevicePath);

                string deviceName = srcName.ViewGdiDeviceName?.TrimEnd('\0') ?? "";

                // Scale lives on the source, and the OS reports it as a step count relative
                // to the recommended value for this panel rather than as a percentage.
                var scale = DisplayScaleService.Read(path.SourceInfo.AdapterId, path.SourceInfo.Id);

                results.Add(new DisplayInfo
                {
                    CcdSourceId = path.SourceInfo.Id,
                    CcdTargetId = path.TargetInfo.Id,
                    AdapterId = path.TargetInfo.AdapterId,
                    SourceAdapterId = path.SourceInfo.AdapterId,
                    DeviceName = deviceName,
                    FriendlyName = tgtName.MonitorFriendlyDeviceName?.TrimEnd('\0') ?? "",
                    Manufacturer = edid.Manufacturer,
                    ProductCode = edid.ProductCode,
                    SerialNumber = edid.SerialNumber,
                    YearOfManufacture = edid.YearOfManufacture,
                    NativeWidth = edid.NativeWidth,
                    NativeHeight = edid.NativeHeight,
                    ExtensionBlockCount = edid.ExtensionBlockCount,
                    ConnectionType = path.TargetInfo.OutputTechnology,
                    RegistryKeyPath = edid.RegistryKeyPath,
                    IsActive = true,
                    IsPrimary = deviceName.Equals(primaryDevName, StringComparison.OrdinalIgnoreCase),
                    CurrentWidth = (int)srcWidth,
                    CurrentHeight = (int)srcHeight,
                    CurrentRefreshRate = Math.Round(refreshRate, 1),
                    CurrentRotationDegrees = rotDeg,
                    CurrentScalePercent = scale.IsKnown ? scale.CurrentPercent : ReadScalePercent(deviceName),
                    AvailableScalePercents = scale.IsKnown
                        ? DisplayScaleService.AvailableScales(scale)
                        : DefaultScales,
                    IsScaleSupported = scale.IsKnown,
                    PositionX = posX,
                    PositionY = posY,
                });
            }
        }
        catch
        {
            // Never crash: enumeration can fail on exotic setups
        }

        return results;
    }

    // ── EDID parsing ──────────────────────────────────────────────

    private record EdidIdentity(
        string Manufacturer,
        string ProductCode,
        string SerialNumber,
        int YearOfManufacture,
        int NativeWidth,
        int NativeHeight,
        int ExtensionBlockCount,
        string RegistryKeyPath);

    private static readonly EdidIdentity EmptyEdid = new(
        "Unknown", "", "", 0, 0, 0, 0, "");

    /// <summary>Reads WMI raw EDID blocks and parses identity fields.</summary>
    private static Dictionary<string, EdidIdentity> ReadWmiEdidMap()
    {
        var map = new Dictionary<string, EdidIdentity>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var idSearcher = new ManagementObjectSearcher(@"root\wmi",
                "SELECT * FROM WmiMonitorID");
            foreach (ManagementObject idObj in idSearcher.Get())
            {
                try
                {
                    string instanceName = (idObj["InstanceName"] as string ?? "").Trim();
                    if (string.IsNullOrEmpty(instanceName)) continue;

                    string manufacturer = DecodeUInt16Array(idObj["ManufacturerName"]);
                    string productCode = DecodeUInt16Array(idObj["ProductCodeID"]);
                    string serialNumber = DecodeUInt16Array(idObj["SerialNumberID"]);
                    int year = Convert.ToInt32(idObj["YearOfManufacture"] ?? 0);

                    // Read raw EDID for native resolution and extension block count
                    int nativeW = 0, nativeH = 0, extBlocks = 0;
                    try
                    {
                        var profile = EdidEngineService.ReadHardwareProfile(instanceName);
                        if (profile != null && profile.Blocks.Count > 0)
                        {
                            extBlocks = profile.Blocks[0].Data[126];
                            ParseNativeResolution(profile, out nativeW, out nativeH);
                        }
                    }
                    catch { /* EDID parse is best-effort */ }

                    // Registry key from instance name
                    string regKey = BuildRegistryKey(instanceName);

                    map[instanceName] = new EdidIdentity(
                        manufacturer, productCode, serialNumber, year,
                        nativeW, nativeH, extBlocks, regKey);
                }
                catch { /* skip individual monitors that fail */ }
            }
        }
        catch { /* WMI unavailable */ }
        return map;
    }

    /// <summary>Reads a raw EDID block via WmiMonitorDescriptorMethods.</summary>
    private static byte[]? ReadRawEdidBlock(string instanceName, byte blockIndex)
    {
        try
        {
            // WmiMonitorDescriptorMethods is a method class - invoke WmiGetMonitorRawEEdidV1Block
            string escapedInstance = instanceName.Replace("\\", "\\\\");
            using var classObj = new ManagementObject(
                $@"root\wmi:WmiMonitorDescriptorMethods.InstanceName='{escapedInstance}'");

            var inParams = classObj.GetMethodParameters("WmiGetMonitorRawEEdidV1Block");
            inParams["BlockId"] = blockIndex;

            var outParams = classObj.InvokeMethod("WmiGetMonitorRawEEdidV1Block", inParams, null);
            if (outParams != null && outParams["BlockContent"] is Array arr)
            {
                byte[] block = new byte[arr.Length];
                for(int i = 0; i < arr.Length; i++) block[i] = Convert.ToByte(arr.GetValue(i));
                return block;
            }
        }
        catch { }
        return null;
    }

    private static void ParseNativeResolution(EdidProfile profile, out int width, out int height)
    {
        width = 0; height = 0;
        foreach (var block in profile.Blocks)
        {
            if (!block.IsExtension)
            {
                // check base block 4 DTDs
                for(int i=0; i<4; i++) {
                    int offset = 54 + (i*18);
                    int pclk = block.Data[offset] | (block.Data[offset+1] << 8);
                    if (pclk != 0) {
                        width = block.Data[offset + 2] | ((block.Data[offset + 4] & 0xF0) << 4);
                        height = block.Data[offset + 5] | ((block.Data[offset + 7] & 0xF0) << 4);
                        return;
                    }
                }
            }
            else
            {
                // check CTA-861 extension block tags
                if (block.Data[0] == 0x02)
                {
                    int dtdOffset = block.Data[2];
                    if (dtdOffset > 0 && dtdOffset < 127)
                    {
                        for (int offset = dtdOffset; offset <= 127 - 18; offset += 18)
                        {
                            int pclk = block.Data[offset] | (block.Data[offset+1] << 8);
                            if (pclk != 0) {
                                width = block.Data[offset + 2] | ((block.Data[offset + 4] & 0xF0) << 4);
                                height = block.Data[offset + 5] | ((block.Data[offset + 7] & 0xF0) << 4);
                                return;
                            }
                        }
                    }
                }
            }
        }
    }

    /// <summary>Decodes a WMI UInt16 char array into a string.</summary>
    private static string DecodeUInt16Array(object? value)
    {
        if (value is not System.Collections.IEnumerable enumerable) return "";
        var sb = new StringBuilder();
        foreach (var c in enumerable)
        {
            ushort u = Convert.ToUInt16(c);
            if (u == 0) break;
            sb.Append((char)u);
        }
        return sb.ToString().Trim();
    }

    /// <summary>Builds the registry key path from the WMI instance name.</summary>
    private static string BuildRegistryKey(string instanceName)
    {
        // Instance name format: DISPLAY\XEC2451\5&1234...&0&UID12345_0
        // Registry: HKLM\SYSTEM\CurrentControlSet\Enum\<instanceName (drop _0 suffix)>
        string cleanName = instanceName;
        int underscoreIdx = cleanName.LastIndexOf('_');
        if (underscoreIdx > 0) cleanName = cleanName[..underscoreIdx];
        return $@"HKLM\SYSTEM\CurrentControlSet\Enum\{cleanName}";
    }

    /// <summary>Matches a CCD monitor device path to a WMI instance name.</summary>
    private static EdidIdentity MatchEdid(Dictionary<string, EdidIdentity> edidMap, string? monitorDevicePath)
    {
        if (string.IsNullOrEmpty(monitorDevicePath)) return EmptyEdid;

        // Monitor device path looks like: \\?\DISPLAY#XEC2451#5&1234...&0&UID12345#{e6f07...}
        // WMI instance name looks like:   DISPLAY\XEC2451\5&1234...&0&UID12345_0
        // Extract the PnP portion for matching
        string normalized = monitorDevicePath
            .Replace("\\\\?\\", "")
            .Replace("#", "\\");

        // Remove the GUID suffix (everything from the last { onward)
        int braceIdx = normalized.LastIndexOf('{');
        if (braceIdx > 0) normalized = normalized[..braceIdx].TrimEnd('\\');

        // Try exact match first
        foreach (var kvp in edidMap)
        {
            string wmiStem = kvp.Key;
            int underscoreIdx = wmiStem.LastIndexOf('_');
            if (underscoreIdx > 0) wmiStem = wmiStem[..underscoreIdx];

            if (normalized.Equals(wmiStem, StringComparison.OrdinalIgnoreCase))
                return kvp.Value;
        }

        // Fallback: match by PnP ID (first two segments)
        string[] normParts = normalized.Split('\\');
        if (normParts.Length >= 2)
        {
            string pnpPrefix = $"{normParts[0]}\\{normParts[1]}";
            foreach (var kvp in edidMap)
            {
                if (kvp.Key.StartsWith(pnpPrefix, StringComparison.OrdinalIgnoreCase))
                    return kvp.Value;
            }
        }

        return EmptyEdid;
    }

    /// <summary>Gets the GDI device name of the primary display.</summary>
    private static string GetPrimaryDeviceName()
    {
        var dm = new DEVMODEW { dmSize = (ushort)Marshal.SizeOf<DEVMODEW>() };
        for (uint i = 0; EnumDisplaySettingsExW(null, i, ref dm, 0); i++)
        {
            // EnumDisplaySettingsEx with null device + ENUM_CURRENT_SETTINGS doesn't work here;
            // use QueryDisplayConfig instead.
        }

        // Simpler: primary is at position (0,0)
        dm = new DEVMODEW { dmSize = (ushort)Marshal.SizeOf<DEVMODEW>() };
        // Iterate device names
        for (int idx = 0; idx < 16; idx++)
        {
            string devName = $@"\\.\DISPLAY{idx + 1}";
            dm = new DEVMODEW { dmSize = (ushort)Marshal.SizeOf<DEVMODEW>() };
            if (EnumDisplaySettingsExW(devName, ENUM_CURRENT_SETTINGS, ref dm, 0))
            {
                if (dm.dmPositionX == 0 && dm.dmPositionY == 0)
                    return devName;
            }
        }
        return @"\\.\DISPLAY1";
    }

    /// <summary>
    /// Fallback scale list for displays whose DPI-scale packet the OS refused. The OS
    /// offers these universally, so listing them beats showing an empty picker; the
    /// picker stays disabled for these displays via <see cref="DisplayInfo.IsScaleSupported"/>.
    /// </summary>
    private static readonly IReadOnlyList<int> DefaultScales =
        new[] { 100, 125, 150, 175, 200, 225, 250, 300 };

    /// <summary>Reads the current DPI scale percentage for a display.</summary>
    private static int ReadScalePercent(string deviceName)
    {
        try
        {
            var dm = new DEVMODEW { dmSize = (ushort)Marshal.SizeOf<DEVMODEW>() };
            if (!EnumDisplaySettingsExW(deviceName, ENUM_CURRENT_SETTINGS, ref dm, 0))
                return 100;

            // Get the monitor handle at the display's position
            var pt = new POINTL { X = dm.dmPositionX + 1, Y = dm.dmPositionY + 1 };
            IntPtr hMonitor = MonitorFromPoint(pt, MONITOR_DEFAULTTONEAREST);
            if (hMonitor == IntPtr.Zero) return 100;

            int hr = GetDpiForMonitor(hMonitor, MONITOR_DPI_TYPE.MDT_EFFECTIVE_DPI, out uint dpiX, out _);
            if (hr != 0) return 100;

            return (int)Math.Round(dpiX / 96.0 * 100);
        }
        catch
        {
            return 100;
        }
    }
}
