using System;
using System.Linq;
using NvAPIWrapper;
using NvAPIWrapper.Display;

public class Program
{
    public static void Main()
    {
        NVIDIA.Initialize();
        var displays = Display.GetDisplays();
        var paths = PathInfo.GetDisplaysConfig();
        
        foreach(var d in displays)
        {
            Console.WriteLine("--- Display " + d.Name);
            var props = d.GetType().GetProperties();
            foreach(var p in props) {
                try {
                    Console.WriteLine("    " + p.Name + " = " + p.GetValue(d));
                } catch { }
            }
        }
    }
}
