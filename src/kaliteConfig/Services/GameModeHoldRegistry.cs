// ==============================================================================
// Copyright (c) 2026 kaliteConfig
// All rights reserved.
//
// This software and associated documentation files are proprietary.
// You may not use, copy, reproduce, modify, merge, publish, distribute, sublicense,
// reverse-engineer, or sell copies of the software in any form, in whole or in part,
// without the express written permission of the copyright holder.
// ==============================================================================
using System;
using System.Collections.Generic;
using System.Linq;

namespace kaliteConfig.Services;

/// <summary>Who is holding the Gaming mode session open.</summary>
public enum GameModeOwner
{
    /// <summary>The Threads page's "Enable Gaming mode" button.</summary>
    UserInterface,

    /// <summary>An enabled rule with "Automatic Gaming mode" ticked.</summary>
    Rule,

    /// <summary>The benchmark's A/B harness.</summary>
    Benchmark,
}

/// <summary>One caller's claim on the session.</summary>
/// <param name="Sequence">Insertion order, so the UI can show holds oldest-first
/// without depending on clock resolution.</param>
public sealed record GameModeHold(
    GameModeOwner Owner,
    int TargetPid,
    string TargetName,
    DateTime AcquiredUtc,
    string Reason,
    long Sequence);

/// <summary>What the service has to do after a hold was taken.</summary>
public enum GameModeAcquireAction
{
    /// <summary>Nothing was running: activate for this target.</summary>
    Activate,

    /// <summary>A session for this very target is already live: just hold it.</summary>
    Joined,

    /// <summary>A session is live for a DIFFERENT target that has exited: tear it
    /// down and activate for this one.</summary>
    HandOff,

    /// <summary>A session is live for a different target that is still running:
    /// hold it and leave the running session alone.</summary>
    JoinedOtherTarget,
}

public readonly record struct GameModeAcquireResult(
    GameModeAcquireAction Action,
    GameModeHold Hold,
    int? RunningTargetPid);

public readonly record struct GameModeReleaseResult(bool HadHold, bool SessionEnded);

/// <summary>
/// Holds-only bookkeeping for the Gaming mode session: no Win32, no UI, so the
/// rules below are unit-testable.
///
/// Why this exists: the session used to be a bare boolean. Any caller could
/// activate (the second one silently re-demoted everything into its own restore
/// map while the shared orchestrator session no-op'd), and ANY caller could
/// deactivate - so whichever finished first tore down the session for the
/// others and stranded their changes. Now the session lives exactly as long as
/// somebody holds it, and it is torn down by the LAST release, not the first.
///
/// The target is decided once, by whoever gets there first. A later hold for a
/// different process joins the running session rather than pulling the rug out
/// from under a game that is currently playing; the session only moves when the
/// process it was started for is gone.
/// </summary>
public sealed class GameModeHoldRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<GameModeOwner, GameModeHold> _holds = new();
    private int? _sessionTargetPid;
    private long _sequence;

    /// <summary>True while at least one caller holds the session.</summary>
    public bool IsHeld
    {
        get { lock (_gate) return _holds.Count > 0; }
    }

    public int Count
    {
        get { lock (_gate) return _holds.Count; }
    }

    /// <summary>The process the live session was started for; null when idle.</summary>
    public int? SessionTargetPid
    {
        get { lock (_gate) return _sessionTargetPid; }
    }

    /// <summary>Holds, oldest first.</summary>
    public IReadOnlyList<GameModeHold> Holds
    {
        get { lock (_gate) return _holds.Values.OrderBy(h => h.Sequence).ToList(); }
    }

    public bool IsHeldBy(GameModeOwner owner)
    {
        lock (_gate) return _holds.ContainsKey(owner);
    }

    /// <summary>True when at least one owner <em>other than</em> this one holds the session.</summary>
    public bool IsHeldByOther(GameModeOwner owner)
    {
        lock (_gate) return _holds.Keys.Any(k => k != owner);
    }

    /// <summary>
    /// Takes (or refreshes) this owner's hold and reports what should happen to
    /// the session. <paramref name="isAlive"/> decides whether the running
    /// session's target may be replaced - injected so the policy is testable.
    /// </summary>
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

            // Re-acquiring with the same owner replaces the hold (a rule whose
            // armed set moved, or the page picking a new row) without inflating
            // the count.
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

    /// <summary>
    /// Drops this owner's hold. <c>SessionEnded</c> is true only when it was the
    /// last one, which is the caller's signal to restore everything.
    /// </summary>
    public GameModeReleaseResult Release(GameModeOwner owner)
    {
        lock (_gate)
        {
            if (!_holds.Remove(owner))
            {
                return new GameModeReleaseResult(false, false); // idempotent
            }

            if (_holds.Count > 0)
            {
                return new GameModeReleaseResult(true, false);
            }

            _sessionTargetPid = null;
            return new GameModeReleaseResult(true, true);
        }
    }

    /// <summary>Drops every hold, reporting whether a session was actually live.</summary>
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
