using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using kaliteConfig.Input.Interop;

namespace kaliteConfig.Input;

internal static class UsbTree
{
    private sealed class Node
    {
        public string Description = string.Empty;
        public string ClassName = string.Empty;
        public string Parent = string.Empty;
        public string Location = string.Empty;
    }

    public static void Apply(IReadOnlyList<MouseDevice> devices)
    {
        var nodes = ReadNodes();
        if (nodes.Count == 0) return;
        foreach (var device in devices)
        {
            try
            {
                var up = new List<string>();
                var locations = new List<string>();
                int hubs = 0;
                string current = device.ParentId;
                for (int level = 0; level < 10 && current.Length > 0; level++)
                {
                    if (!nodes.TryGetValue(current, out var node)) break;
                    string label = node.Description.Length > 0
                        ? node.Description
                        : node.ClassName.Length > 0 ? node.ClassName : string.Empty;
                    if (label.Length > 0)
                    {
                        up.Add(label);
                        if (label.IndexOf("hub", StringComparison.OrdinalIgnoreCase) >= 0) hubs++;
                        if (node.Location.Length > 0) locations.Add(FormatLocation(node.Location));
                    }
                    current = node.Parent;
                }
                if (up.Count == 0) continue;
                device.PortLabel = PickPort(locations);
                up.Reverse();
                device.HubDepth = Math.Max(0, hubs - 1);
                string chain = string.Join(" › ", up);
                string name = device.Product.Length > 0 ? device.Product : device.DisplayName;
                if (name.Length > 0) chain += " › " + name;
                if (device.PortLabel.Length > 0) chain += " (" + device.PortLabel + ")";
                device.PortChain = chain;
            }
            catch
            {
            }
        }
    }

    private static string PickPort(List<string> locations)
    {
        foreach (string location in locations)
            if (location.IndexOf("Port", StringComparison.OrdinalIgnoreCase) >= 0) return location;
        return locations.Count > 0 ? locations[0] : string.Empty;
    }

    private static string FormatLocation(string location)
    {
        string text = location.Replace("Port_#", "Port ").Replace(".Hub_#", " · hub ");
        var parts = text.Split(' ');
        var clean = new List<string>();
        foreach (string part in parts)
        {
            if (part.Length == 0) continue;
            string trimmed = part.TrimStart('0');
            clean.Add(trimmed.Length == 0 ? "0" : trimmed);
        }
        return string.Join(" ", clean).Trim();
    }

    private static Dictionary<string, Node> ReadNodes()
    {
        var map = new Dictionary<string, Node>(StringComparer.OrdinalIgnoreCase);
        Guid empty = Guid.Empty;
        IntPtr set = NativeInput.SetupDiGetClassDevsW(ref empty, null, IntPtr.Zero,
            NativeInput.DigcfPresent | NativeInput.DigcfAllClasses);
        if (set == IntPtr.Zero || set == new IntPtr(-1)) return map;
        int size = IntPtr.Size == 8 ? 32 : 28;
        IntPtr devinfo = Marshal.AllocHGlobal(size);
        try
        {
            uint index = 0;
            while (true)
            {
                for (int i = 0; i < size; i++) Marshal.WriteByte(devinfo, i, 0);
                Marshal.WriteInt32(devinfo, 0, size);
                if (!NativeInput.SetupDiEnumDeviceInfo(set, index, devinfo)) break;
                index++;
                string instance = ReadInstance(set, devinfo);
                if (instance.Length == 0) continue;
                var node = new Node
                {
                    Description = ReadString(set, devinfo, NativeInput.SpdrpFriendlyName),
                    ClassName = ReadString(set, devinfo, NativeInput.SpdrpClassName),
                    Location = ReadString(set, devinfo, NativeInput.SpdrpLocationInfo),
                    Parent = ReadParent(set, devinfo),
                };
                if (node.Description.Length == 0)
                    node.Description = ReadString(set, devinfo, NativeInput.SpdrpDeviceDesc);
                map[instance] = node;
            }
        }
        catch
        {
        }
        finally
        {
            Marshal.FreeHGlobal(devinfo);
            try { NativeInput.SetupDiDestroyDeviceInfoList(set); } catch { }
        }
        return map;
    }

    private static string ReadInstance(IntPtr set, IntPtr devinfo)
    {
        try
        {
            IntPtr buffer = Marshal.AllocHGlobal(1024);
            try
            {
                if (!NativeInput.SetupDiGetDeviceInstanceIdW(set, devinfo, buffer, 1024, out _)) return string.Empty;
                return Marshal.PtrToStringUni(buffer) ?? string.Empty;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string ReadString(IntPtr set, IntPtr devinfo, uint property)
    {
        try
        {
            uint needed = 0;
            NativeInput.SetupDiGetDeviceRegistryPropertyW(set, devinfo, property, out _, IntPtr.Zero, 0, out needed);
            if (needed < 2) return string.Empty;
            IntPtr buffer = Marshal.AllocHGlobal(checked((int)needed));
            try
            {
                if (!NativeInput.SetupDiGetDeviceRegistryPropertyW(set, devinfo, property, out _, buffer, needed, out _))
                    return string.Empty;
                return (Marshal.PtrToStringUni(buffer) ?? string.Empty).Trim();
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string ReadParent(IntPtr set, IntPtr devinfo)
    {
        try
        {
            var key = new NativeInput.DevPropKey
            {
                Fmtid = NativeInput.DevpkeyDeviceParentFmtid,
                Pid = NativeInput.DevpkeyDeviceParentPid,
            };
            IntPtr buffer = Marshal.AllocHGlobal(1024);
            try
            {
                if (!NativeInput.SetupDiGetDevicePropertyW(set, devinfo, ref key, out _, buffer, 1024, out _, 0))
                    return string.Empty;
                return Marshal.PtrToStringUni(buffer) ?? string.Empty;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        catch
        {
            return string.Empty;
        }
    }
}
