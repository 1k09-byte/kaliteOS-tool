using System;
using System.Collections.Generic;

namespace kaliteConfig.Input;

public interface IMouseGuardPolicy
{
    bool RateAllows(string deviceId);
    bool RangeOk(double value, double minimum, double maximum);
    (bool Allowed, string Reason) PrimaryClickStays(IReadOnlyDictionary<int, string> buttonMap);
    void NoteOnboardWrite(string deviceId);
    int OnboardWriteCount(string deviceId);
}

public sealed class MouseGuardPolicy : IMouseGuardPolicy
{
    private readonly object _gate = new();
    private readonly Dictionary<string, List<long>> _writes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _onboardWrites = new(StringComparer.OrdinalIgnoreCase);
    private static readonly long TickLimit = TimeSpan.FromSeconds(5).Ticks;
    private const int MaxWritesPerWindow = 4;

    public bool RateAllows(string deviceId)
    {
        if (string.IsNullOrEmpty(deviceId)) return false;
        long now = DateTime.UtcNow.Ticks;
        lock (_gate)
        {
            if (!_writes.TryGetValue(deviceId, out var stamps))
            {
                stamps = new List<long>();
                _writes[deviceId] = stamps;
            }
            stamps.RemoveAll(t => now - t > TickLimit * 12);
            long windowStart = now - TickLimit;
            int recent = 0;
            foreach (long stamp in stamps)
                if (stamp >= windowStart) recent++;
            if (recent >= MaxWritesPerWindow) return false;
            stamps.Add(now);
            return true;
        }
    }

    public bool RangeOk(double value, double minimum, double maximum)
    {
        if (double.IsNaN(value) || double.IsInfinity(value)) return false;
        return value >= minimum && value <= maximum;
    }

    public (bool Allowed, string Reason) PrimaryClickStays(IReadOnlyDictionary<int, string> buttonMap)
    {
        if (buttonMap is null || buttonMap.Count == 0)
            return (false, "Blocked by guard: the button map is empty, so no button would perform a primary click.");
        foreach (var pair in buttonMap)
            if (string.Equals(pair.Value, "PrimaryClick", StringComparison.OrdinalIgnoreCase))
                return (true, string.Empty);
        return (false, "Blocked by guard: the change leaves no button performing a primary click, so it was not applied.");
    }

    public void NoteOnboardWrite(string deviceId)
    {
        if (string.IsNullOrEmpty(deviceId)) return;
        lock (_gate)
        {
            _onboardWrites.TryGetValue(deviceId, out int count);
            _onboardWrites[deviceId] = count + 1;
        }
    }

    public int OnboardWriteCount(string deviceId)
    {
        if (string.IsNullOrEmpty(deviceId)) return 0;
        lock (_gate) return _onboardWrites.TryGetValue(deviceId, out int count) ? count : 0;
    }
}
