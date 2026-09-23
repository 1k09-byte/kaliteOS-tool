using System;
using kaliteConfig.Services;

namespace NetVerify;

internal static class SpeedTests
{
    public static void Run(Action<bool, string> check)
    {
        check(NetSpeedTest.FormatMbps(250.7) == "251 Mbps", "speed formats hundreds");
        check(NetSpeedTest.FormatMbps(35.44) == "35.4 Mbps", "speed formats tens");
        check(NetSpeedTest.FormatMbps(9.876) == "9.88 Mbps", "speed formats singles");
        check(NetSpeedTest.FormatMbps(0) == "0 Mbps", "speed formats zero");
        check(NetSpeedTest.FormatMbps(double.NaN) == "-", "speed guards NaN");
        check(NetSpeedTest.FormatMbps(-1) == "-", "speed guards negative");
    }
}
