using System;
using System.Linq;
using NvAPIWrapper;

namespace kaliteConfig
{
    class Program2
    {
        static void Main()
        {
            try
            {
                NVIDIA.Initialize();
                var displays = NvAPIWrapper.Display.Display.GetDisplays();
                Console.WriteLine("Displays found: " + displays.Length);
                foreach(var disp in displays)
                {
                    Console.WriteLine("Name: " + disp.Name);
                    
                    var dvc = disp.DigitalVibranceControl;
                    Console.WriteLine($"  DVC Level: {dvc.CurrentLevel} (Min: {dvc.MinimumLevel}, Max: {dvc.MaximumLevel})");
                    
                    var hue = disp.HUEControl;
                    Console.WriteLine($"  HUE Angle: {hue.CurrentAngle} (Min: {hue.MinimumAngle}, Max: {hue.MaximumAngle})");

                    var color = disp.ColorData;
                    // I will check the property names dynamically. 
                }
            }
            catch(Exception e)
            {
                Console.WriteLine("Error: " + e.Message);
            }
        }
    }
}
