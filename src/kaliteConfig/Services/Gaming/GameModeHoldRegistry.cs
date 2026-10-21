using System;
using System.Collections.Generic;
using System.Linq;

namespace kaliteConfig.Services;

public enum GameModeOwner
{
    UserInterface,

    Rule,
}

public sealed record GameModeHold(
    GameModeOwner Owner,
    int TargetPid,
    string TargetName,
    DateTime AcquiredUtc,
    string Reason,
    long Sequence);

public enum GameModeAcquireAction
{
    Activate,

    Joined,

    HandOff,

    JoinedOtherTarget,
}

public readonly record struct GameModeAcquireResult(
    GameModeAcquireAction Action,
    GameModeHold Hold,
    int? RunningTargetPid);

public readonly record struct GameModeReleaseResult(bool HadHold, bool SessionEnded);

public sealed class GameModeHoldRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<GameModeOwner, GameModeHold> _holds = new();
    private int? _sessionTargetPid;
    private long _sequence;

    public bool IsHeld
    {
        get { lock (_gate) return _holds.Count > 0; }
    }

    public int Count
    {
        get { lock (_gate) return _holds.Count; }
    }

    public int? SessionTargetPid
    {
        get { lock (_gate) return _sessionTargetPid; }
    }

    public IReadOnlyList<GameModeHold> Holds
    {
        get { lock (_gate) return _holds.Values.OrderBy(h => h.Sequence).ToList(); }
    }

    public bool IsHeldBy(GameModeOwner owner)
    {
        lock (_gate) return _holds.ContainsKey(owner);
    }

    public bool IsHeldByOther(GameModeOwner owner)
    {
        lock (_gate) return _holds.Keys.Any(k => k != owner);
    }

    public GameModeAcquireResult Acquire(
        GameModeOwner owner,
        int targetPid,
        string targetName,
        string reason,
        Func<int, bool> isAlive)
    {
        lock (_gate)
        {
            bool firstForOwner = !_holds.TryGetValue(owner, out var existing);
            var hold = new GameModeHold(
                owner,
                targetPid,
                targetName,
                DateTime.UtcNow,
                reason,
                firstForOwner ? ++_sequence : existing!.Sequence);

            _holds[owner] = hold;

            int? running = _sessionTargetPid;
            if (running == null)
            {
                _sessionTargetPid = targetPid;
                return new GameModeAcquireResult(GameModeAcquireAction.Activate, hold, null);
            }

            if (running.Value == targetPid)
            {
                return new GameModeAcquireResult(GameModeAcquireAction.Joined, hold, running);
            }

            if (isAlive(running.Value))
            {
                return new GameModeAcquireResult(GameModeAcquireAction.JoinedOtherTarget, hold, running);
            }

            _sessionTargetPid = targetPid;
            return new GameModeAcquireResult(GameModeAcquireAction.HandOff, hold, running);
        }
    }

    public GameModeReleaseResult Release(GameModeOwner owner)
    {
        lock (_gate)
        {
            if (!_holds.Remove(owner))
            {
                return new GameModeReleaseResult(false, false);
            }

            if (_holds.Count > 0)
            {
                return new GameModeReleaseResult(true, false);
            }

            _sessionTargetPid = null;
            return new GameModeReleaseResult(true, true);
        }
    }

    public bool Clear()
    {
        lock (_gate)
        {
            bool wasHeld = _holds.Count > 0;
            _holds.Clear();
            _sessionTargetPid = null;
            return wasHeld;
        }
    }
}
