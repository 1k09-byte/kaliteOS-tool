using System;
using System.Runtime.InteropServices;

namespace kaliteConfig.Native
{
    public static class DwmApi
    {
        [DllImport("dwmapi.dll")]
        public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);
    }
}
