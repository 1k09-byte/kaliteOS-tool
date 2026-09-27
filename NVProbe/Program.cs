using System;
using System.Linq;
using NvAPIWrapper.Display;
using NvAPIWrapper.Native;
using NvAPIWrapper.Native.Display;
using NvAPIWrapper.Native.Exceptions;

class Program
{
    static void Main()
    {
        try
        {
            NvAPIWrapper.NVIDIA.Initialize();
            Console.WriteLine("NVAPI Initialized.");

            var displays = DisplayDevice.GetDisplayDevices();
            if (displays.Length == 0)
            {
                Console.WriteLine("No displays found");
                return;
            }

            foreach(var d in displays)
            {
                Console.WriteLine($"Display: {d.Name}");

                try {
                    var dvc = d.DigitalVibranceControl;
                    Console.WriteLine($"  DVC (Vibrance): Current={dvc.CurrentLevel}, Max={dvc.MaximumLevel}, Min={dvc.MinimumLevel}, Default={dvc.DefaultLevel}");
                } catch(Exception e) { Console.WriteLine("  DVC NOT SUPPORTED: " + e.Message); }

                try {
                    var color = d.ColorData;
                    Console.WriteLine($"  Color (Gamma/Bright): ColorData queried successfully");
                } catch(Exception e) { Console.WriteLine("  ColorData NOT SUPPORTED: " + e.Message); }

                try {
                    var scaling = DisplayAPI.GetDisplayFeatureConfig(d.DisplayId);
                    Console.WriteLine($"  Scaling: ... success");
                } catch(Exception e) { Console.WriteLine("  Scaling API NOT SUPPORTED: " + e.Message); }

                try {
                    var depth = DisplayAPI.GetColorData(d.DisplayId);
                    Console.WriteLine($"  ColorDepth/Format API: success");
                } catch(Exception e) { Console.WriteLine("  ColorDepth API NOT SUPPORTED: " + e.Message); }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex.ToString());
        }
    }
}
