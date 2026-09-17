namespace kaliteConfig.GpuOverclock.Models
{
    /// <summary>
    /// Canonical control names. Used by the ViewModel when queueing writes and
    /// by SafetyRevertService when reverting — the revert switch matches on
    /// these, so both sides must stay in lockstep.
    /// </summary>
    public static class OcControlNames
    {
        public const string CoreClockOffset = "Core clock offset";
        public const string MemoryClockOffset = "Memory clock offset";
        public const string PowerLimit = "Power limit";
        public const string TemperatureLimit = "Temperature limit";
        public const string FanSpeed = "Fan speed";
    }
}
