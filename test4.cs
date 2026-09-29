using System;
using System.Reflection;

public class Program
{
    public static void Main()
    {
        var asm = Assembly.LoadFrom(@"c:\Users\Administrator\source\repos\kaliteconfig\src\kaliteConfig\bin\x64\Debug\net10.0-windows10.0.22621.0\win-x64\NvAPIWrapper.dll");
        var dispType = asm.GetType("NvAPIWrapper.Display.Display");
        foreach(var p in dispType.GetProperties()) 
            Console.WriteLine("P: {0} ({1})", p.Name, p.PropertyType.Name);
        var apiType = asm.GetType("NvAPIWrapper.Native.DisplayApi");
        foreach(var m in apiType.GetMethods()) 
            Console.WriteLine("M: {0}", m.Name);
    }
}
