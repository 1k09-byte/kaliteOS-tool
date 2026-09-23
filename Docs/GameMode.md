# Game Mode: what it does now, and what was removed

Scope: `Services/GamingModeService.cs`, `Services/ForegroundSuspendService.cs`,
`ProcessOptimizer/Services/ForegroundBoosterService.cs`,
`ProcessOptimizer/Services/BackgroundThrottleService.cs`,
`ProcessOptimizer/Services/OptimizationSessionOrchestrator.cs`,
`ProcessOptimizer/Models/OptimizationModels.cs`,
`ProcessOptimizer/Services/ProtectedProcessGuard.cs`,
`Services/ProcessTuningService.cs`, `Pages/ThreadTunerPage.xaml`,
`Controls/RuleEditorDialog.xaml.cs`, `ProcessOptimizer/Services/ContentionPolicy.cs`,
`ProcessOptimizer/Services/ContentionMonitor.cs`, `Native/SystemProcessCpuReader.cs`,
`tools/BoostVerify`.

Stage 0 was stopping the bleeding: what Game Mode does, and what it no longer
does. Stage 1 is complete — one refcounted session (§6) and the contention poll
replaced by a single system call with hysteresis and a minimum dwell (§7). Still
ahead: the Stage 2 per-lever toggles, each measured by the A/B harness.

## 1. Why this was done: Game Mode was halving the machine

The development box is an **AMD Ryzen 7 9700X, 8C/16T, homogeneous** — the app's
own `TopologyService` documents "non-hybrid CPUs: all 0" for `EfficiencyClass` —
and it has a kernel CPU-set reservation of `0xFC00`, i.e. **logical processors
10-15** (`tools/ThreadVerify` prints the same thing: "Usable CPUs: 0-9
(0x3FF)").

Traced through the old `CpuSetPartitionService.ApplyExclusivePartition`:

| Step | Result |
|---|---|
| `pSets` = all sets (no efficiency classes to split on) | LPs 0-9 (10-15 reserved out) |
| `usable` = `pSets` minus core 0 | LPs 2-9 (**8**) |
| `gameSets` | LPs 2-9 |
| `bgSets` = topology − game − reserved | **LPs 0-1, core 0 alone** |

Then `ForegroundBoosterService` *also* wrote `proc.ProcessorAffinity = gameMask`,
a different 8-LP set (one primary thread per physical core, including core 0).
The intersection is what the game actually got — roughly **three physical cores**
— while every other process on the box was pinned to core 0, the DPC/interrupt
core, and 6 of 16 LPs sat idle. That is the opposite of what Game Mode promises.

Roblox was armed for this twice over: `threadtuner-profiles.json` carries
`GamingModeAuto: true` and `suspend-mode.json` carries `Auto: true`.

## 2. What Game Mode ON does now — the whole of it

| Lever | Target process | Background |
|---|---|---|
| Priority class | `AboveNormal` (never High) | untouched |
| EcoQoS | forced OFF | ON |
| Memory / I/O | priority 5 / Normal | 2 / VeryLow, then 1 / VeryLow at Moderate |
| CPU sets & affinity | untouched | untouched |
| Threads | untouched | untouched (memory priority only, at Moderate) |
| Boost flag | untouched | untouched |

Only background processes using **≥2% of total machine CPU** (`GamingMode`'s
`ContentionPercentOfTotalCpu`, measured once for the whole machine) are demoted
at all. An idle process does not compete with a game running at AboveNormal, and
writing to hundreds of them churned the scheduler and the restore map for
nothing.

## 3. Removed, and why

- **The CPU-set partition.** `CpuSetPartitionService` is deleted outright; it had
  no callers once the clamping went. See the table above for the arithmetic.
- **The affinity clamp.** Clipping a game to one thread per physical core caps a
  multithreaded engine at half the machine's threads, and it intersected with the
  partition to leave ~3 cores.
- **Thread rewriting.** The booster raised the 3 hottest threads to Highest and
  pinned their ideal processors. Engines set thread priorities on purpose (audio,
  streaming, worker pools); this fought them. Kept as a Stage 2 A/B candidate,
  not a default.
- **The whole Aggressive tier.** It added a per-process Job Object CPU rate cap —
  and a hard cap cannot be lifted off a process mid-session, because a process
  cannot leave the job it was assigned to; only a global disable at session end
  helped, and that path early-returned whenever no session was active. Its
  affinity fallback was worse: `GetEfficiencyFallbackAffinity()` returns the
  highest logical processor on a non-hybrid CPU, which on this box is LP 15 — a
  **kernel-reserved** processor the kernel does not schedule user threads on. A
  process pinned there may never run. Both are gone, so `AggressivenessLevel`
  now ends at `Moderate`.
- **The 200 ms-per-process activation sweep.** `GetCpuPercentSnapshot(pid)`
  slept 200 ms inside itself and was called once per candidate process — roughly
  **40 seconds** of serial sleeping inside `ActivateAsync`, with the machine
  churning for the whole first minute of the game. Replaced by
  `SampleCpuPercentOfTotal`: two passes over all processes with a single 250 ms
  sleep, returning each process's share of total CPU.
- **Four handle opens per candidate process.** The demote loop now opens one
  handle and reuses it for the priority read, the eco read and both writes.
- **Boost disabling during a session.** The demote loop disabled each process's
  priority boost, and `Deactivate()` then re-enabled it *unconditionally* — which
  silently reverted a per-process "boost disabled" preference set through the
  Boost column. The 20 s keeper then turned it back off, so the two systems
  fought forever and the setting looked like it kept resetting. The boost flag is
  now simply not touched by either path.

## 4. Bugs found on the way

- **Suspend Mode never consulted the exemption list.** `BuildBackgroundList`
  checked `IsCritical`, `IsSelf` and `ProtectedProcessGuard` but not
  `GamingExemptionService.IsExempt`, so the audio/capture, launcher, overlay and
  anti-cheat tiers — and the user's `gaming-exempt.txt` — were bypassed when
  suspending background threads. Suspending an anti-cheat's threads is a ban
  risk and suspending Discord/OBS breaks voice and capture. It is checked now.
- **Suspend Mode checked a disposed `Process`.** The loop disposed `proc` in a
  `finally` and then, two lines later, called
  `ProtectedProcessGuard.IsProcessProtected(proc.ProcessName)` on it. A disposed
  `Process` cannot be queried, and because the whole enumeration sits inside a
  catch-all, anything it threw silently truncated the background list. The checks
  now use the already-captured `name`.
- **`ProtectedProcessGuard` ignored any name ending in `.exe`.** Its denylist is
  spelled bare (`"dwm"`, `"csrss"`), but `ProcessStateSnapshotService` passed
  `proc.ProcessName + ".exe"` — so the hard denylist silently matched nothing for
  every such caller. The guard now normalises the extension off before matching.
- **Overlapping sessions.** `StartSession` early-returns when one is already
  active and `EndSession` early-returns when none is; the Threads button, the
  Roblox `GamingModeAuto` rule and the benchmark A/B loop could all activate,
  and whichever one *finished* first tore the session down for the others,
  stranding their changes with no holder left. Fixed in §6.

## 5. Verification

```bash
dotnet build kaliteConfig.csproj -p:Platform=x64          # 0 errors
dotnet test kaliteConfig.Tests/kaliteConfig.Tests.csproj  # 145 passed
dotnet run --project kaliteConfig/tools/ThreadVerify      # 7 passed
dotnet run --project kaliteConfig/tools/BoostVerify       # 19 passed, 0 failed
```

`BoostVerify` was rewritten around the new contract, so most of its checks now
assert that Game Mode does *not* do things:

```
— Game process —
  [PASS] priority class == AboveNormal (0x8000): read 0x8000
  [PASS] never raised to High (0x80)
  [PASS] affinity untouched (no core clamp): before 0xFFFF, after 0xFFFF
  [PASS] threads untouched: 0 of 13 threads at Highest
  [PASS] not confined to a CPU-set partition: no explicit sets (inherits the whole machine)
  [PASS] priority-boost flag unchanged
— Background process (Light throttle) —
  [PASS] priority class still Normal (0x20)   ← was the old "known failure"
  [PASS] Efficiency mode ON
```

The old build's single documented failure — `background priority class ==
BelowNormal: read Normal` — was never a `BackgroundThrottleService` bug: a Light
throttle deliberately never changes the priority class, and the assertion
demanded otherwise. The expectation is now the correct one, which is why the
suite is green rather than carrying a permanent exception.

Not exercised headlessly: the Threads page status line and the A/B harness
against a live game. The number that matters — 1% lows with Game Mode on vs off
in Roblox — needs a manual pass.

## 6. One refcounted session (Stage 1)

The session is no longer a boolean. `GameModeHoldRegistry` — pure logic, no
Win32 and no UI, unit-tested in `kaliteconfig.Tests/GameModeHoldTests.cs` — keeps
one claim per caller:

```csharp
public enum GameModeOwner { UserInterface, Rule, Benchmark }
```

- **`AcquireAsync(owner, targetPid, protectedPids, reason)`** takes or refreshes
  that owner's hold and reports what happened: `Activated`, `JoinedSameTarget`,
  `JoinedOtherTarget`, `HandedOff` or `Failed`.
- A second caller for the **same** target **joins** and does not re-run the
  sweep. Re-running is actively harmful: it would snapshot already-demoted
  processes as their "original" state, losing the real baseline and leaving the
  second caller's release restoring the demoted values.
- A caller asking for a **different** target also joins while the session's game
  is still alive — pulling the rug out from under a game that is currently
  playing is worse than ignoring the second caller's target. When the session's
  target has **exited**, the session is handed over (this is how a second armed
  game takes over when the first closes).
- **`Release(owner)`** restores everything only when it was the **last** hold;
  otherwise it drops the hold and reports that the session continues. That is
  the actual fix: a rule going quiet, or the benchmark finishing a pair, can no
  longer restore state somebody else is still holding.
- **`ReleaseAll()`** is the explicit stop-everything path: the page's "Restore"
  button, the update shutdown, and the window's `Closed` handler. Holds never
  outlive the process, or the demoted processes stay lowered with nothing left
  to put them back. It restores unconditionally rather than only when a hold
  existed: CPU-bound auto-raise writes to the same restore map *without* taking
  a hold, so gating on holds would leave the Restore button enabled
  (`RestorableCount > 0`) and then do nothing.
- `IsActive` is now derived ("any hold"), so it cannot disagree with the holds.
- The watcher's old `_watcherInitiatedGamingMode` flag is gone. It existed only
  to stop the watcher deactivating a session the user had started; the Rule hold
  now expresses that directly, per caller rather than globally.

A hold also replaces the old "is it on?" check in the UI: the Threads button
reads and writes only the `UserInterface` hold, and when somebody *else* holds
the session it says so (`Gaming mode ON (held by rule)`).

The benchmark takes a `Benchmark` hold for each ON half and releases it after,
so pairs stay clean. If anything else already holds the session when a run
starts, every run comment is tagged `WARNING: another holder is active — OFF
runs are not truly off` — with a `GamingModeAuto` rule for the same game that is
now the normal case, and a comparison that silently includes Game Mode in both
halves measures nothing.

## 7. The contention poll (Stage 1, second half)

The session monitor decided who was competing for the CPU by calling
`Process.GetProcesses()`, allocating and disposing a `Process` per PID, and then
opening a handle to **every** process once a second — `OpenProcess` +
`GetProcessTimes` + `CloseHandle`, per process, for the whole session. A
"helper" that opens a few hundred handles a second while a game is running is
working against its own purpose.

**It is now one system call per tick.** `Native/SystemProcessCpuReader.cs` walks
`NtQuerySystemInformation(SystemProcessInformation)`, whose records already carry
each process's `UserTime`/`KernelTime`, so the whole machine is read from one
buffer with zero process handles. PIDs fall out of the snapshot for free, so the
separate dead-PID cleanup pass the old monitor needed is gone too.

`PdhContentionMonitor` is deleted. The class never touched PDH; the replacement
is `ContentionMonitor`, and its decision logic lives in `ContentionPolicy` where
it can be tested without a machine to measure.

**Hysteresis and a dwell, because the thrash cost more than the contention.**
The old release rule was "this process missed one sample" — it was restored on
the first quiet tick and demoted again on the next. Each restore is a full
priority/EcoQS/memory/IO pass, so a process hovering near the 3% bar spent the
session being written back and forth.

| Decision | Rule |
|---|---|
| Demote at all | 2 consecutive hot samples (never the first) |
| Escalate to Moderate | 4 consecutive hot samples (ceiling — no Aggressive) |
| Forget prior heat | 2 consecutive calm samples (ladder restarts at Light) |
| Release | 5 consecutive calm samples **and** held ≥ 15 s |

The threshold itself is unchanged: 3% of **total** machine CPU, the same figure
the old monitor used (3% of 16 threads ≈ half of one core held continuously).

**A quiet machine now produces ticks.** This is the bug worth knowing about: the
old monitor only raised its event when at least one process was contended, and
the release pass lived inside that handler. So once everything it had throttled
went quiet, no tick arrived, the release pass never ran, and those processes
stayed demoted for the rest of the session — the same "settings get stranded"
shape as the session-teardown bug in §6. `ComputeTick` therefore always returns
a sample, empty or not, and an empty one is exactly the "everything is calm now"
signal the release pass needs.

Two smaller rules the tick encodes, both of which would have demoted innocent
processes:

- A process appearing in the snapshot with **no prior reading** is skipped. Its
  *lifetime* CPU time would otherwise be attributed to a single 1-second tick, so
  a just-launched process would read as having held a core since boot.
- A **backwards** counter (reused PID, reset counter) is treated as idle rather
  than as a negative or enormous delta.

The orchestrator also prunes hysteresis counters for PIDs absent from the tick's
alive set, so a process that exits cannot leave its heat behind for a later
process that reuses its PID.

Measured cost, from `BoostVerify` (169 processes):

```
[PASS] one syscall returns every process's CPU time: 169 processes from one NtQuerySystemInformation call
[PASS] our own PID is in the snapshot: pid 17724
[PASS] tracks real CPU time: ~300 ms of spinning registered as 281 ms of CPU time
[PASS] cheaper than a handle per process: 0.88 ms per sample, vs 1.65 ms for 169
       OpenProcess/GetProcessTimes pairs (one Process object allocated and disposed each) — 1.9x cheaper
```

The CPU-time check is the one that proves the struct offsets: a wrong offset
would still return a full PID list, but with zero or nonsense times. The cost
ratio lands around 2x and varies with machine load — the win is not speed, it is
that the monitor no longer opens a handle to every process on the box every
second, and no longer allocates a `Process` object per PID.
