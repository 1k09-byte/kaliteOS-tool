# Process Control permanence, NVIDIA component removal, affinity after a GPU restart

Scope: `Services/ProfileWatcherService.cs`, `Services/ProcessBoostPreferenceService.cs`,
`Services/BoostPreferenceService.cs`, `Services/RulePatternGuard.cs`,
`Services/ProcessTuningService.cs`, `Models/ThreadTunerModels.cs`,
`Pages/ThreadTunerPage.xaml(.cs)`, `Services/NvidiaPackageService.cs`,
`Controls/NvidiaComponentPickerDialog.xaml.cs`, `Pages/AffinityPage.xaml.cs`,
`ViewModels/AffinityViewModel.cs`, `Services/AffinityService.cs`,
`Pages/ReservedCpuSetsPage.xaml`.

## 1. The built-in DWM rule never applied (fixed at the source)

The shipped built-in ("Input/Sensor Threads" → `dwm.exe`, Master Input / Kernel
Sensor threads at Time-Critical) sat at **"Waiting for process"** forever. The
saved profile said why:

```json
{ "Name": "Input/Sensor Threads", "Pattern": "Input/Sensor Threads", ... }
```

The process pattern held the *rule name*. `ProfileMatcher` compared it against
process names, matched nothing, and `rules-debug.log` recorded it on every
single start:

```
11:57:23.417 [pid=6972] apply-now: pattern=[Input/Sensor Threads] pids=[]
```

- `RulePatternGuard` (new, pure and unit-tested) detects "pattern == display
  name" and, for built-ins, returns the shipped pattern.
- `EnsureBuiltInDefaultsAsync` repairs only the **pattern** of such a built-in.
  Everything else the user changed (thread affinities, boost flags) is kept —
  the live copy carries a custom `AffinityMask: 21845` that a full refresh would
  have wiped.
- User rules are never rewritten: they get an honest
  `LastResult = "Pattern is the rule name — nothing can match it"` instead of
  looking like they are merely waiting.

## 2. Settings are permanent now: the keeper sweep

A one-shot apply is what made every setting look like it "resets itself": the
process restarts, a thread is created later, Windows re-enables boost, and the
old value is back.

`ProfileWatcherService.StartKeeper()` (started from `App.InitializeWatcherAsync`)
runs every **20 s** and:

1. re-applies every enabled + auto-apply rule to the processes that are running
   (`ApplyProfileNowAsync(profile, quiet: true)`),
2. re-arms the persisted **per-thread** boost suppressions
   (`BoostPreferenceService.ApplyToRunningProcessesAsync`),
3. re-applies the persisted **per-process** boost preferences
   (`ProcessBoostPreferenceService.ApplyToRunningProcessesAsync`).

`quiet: true` means the result line is written without a timestamp, and the rules
file is only saved + the rules list only repainted when the text or a thread
target count actually changed — a background sweep must not flicker the UI or
rewrite the JSON forever. The pass is re-entrancy guarded so a slow one cannot
stack.

## 3. Priority-boost tick box on every process row

The Processes list gained a **Boost** column: ticked = Windows default, unticked
= priority boost disabled for the whole process.

- Ticking applies `SetProcessPriorityBoost` to **every live instance** of that
  process immediately (`ProcessBoostPreferenceService.SetAsync`).
- The choice is stored in `%LOCALAPPDATA%\kaliteConfig\process-boost-prefs.json`
  (normalised to the process name without `.exe`), re-applied by the keeper and
  by the process-start watcher, so it survives restarts.
- The right-click **Priority boost >** submenu goes through the same path — it
  used to be a one-shot `SetPriorityBoostAsync` that Windows forgot.
- The live state is read in `ProcessTuningService.FillStatic` from the process
  handle that was already open, so showing the column costs nothing extra.
- Protected processes render the box disabled; a refused write pops the tick
  back and says so.
- The service writes through a `ProcessBoostPreferenceService.Applier` delegate
  that `App` installs at startup, which keeps the store free of the WinUI
  application object and therefore unit-testable.

## 4. NVIDIA: unchecked components are actually removed now

`ApplySelectionAsync` computed its exclusion set from the list it was handed —
and the picker handed over **only the ticked components**, so the set was always
empty and every "useless" extra (NvApp, telemetry, ShadowPlay, container
services…) installed anyway. Two bugs, both fixed:

- `NvidiaComponentVm.IsSelected` now writes through to the parsed
  `NvidiaComponent` (`Model.IsSelected` was never updated, so even the tick
  state it did pass was stale).
- The dialog hands over **every** component with its final selection.

`ApplySelectionAsync` now, per deselected component:

- marks the sub-package `disposition="hidden"`,
- removes child entries that provably name payload inside that component's own
  extracted folder,
- deletes that component's extracted folder (skipped when a kept component
  resolves to the same folder, when it is the extraction root, or when it would
  escape it),
- logs exactly what was excluded, so the install log is evidence rather than a
  claim.

Two deliberate safety rules: `userSelectable="false"` (installer-managed)
sub-packages are listed but never excluded — the installer needs them to resolve
what it does install — and components missing from the known list default to
**ticked**, because silently dropping an unnamed platform component breaks
hardware features (laptop Dynamic Boost) while a stray optional one is one tick
away from off.

## 5. Affinity page after a GPU restart

A GPU restart (`pnputil /restart-device`) or a driver install re-enumerates the
adapter, which left the cached page holding rows whose interrupt registry keys
no longer existed: reads came back empty and writes landed nowhere.

- The page rescans on **every** visit (it is a cached page, so it previously
  scanned only when all four lists were empty).
- `AffinityService.DeviceExists` (via the new shared `ResolveFullInstanceId`
  helper, which `RestartDeviceAsync` also uses) tells a live row from a stale one.
- Opening a stale row reports "no longer present", rescans and does not show an
  empty dialog; **Apply** refuses to write to a device that is gone.
- The selected device is matched by instance id across rescans, and after a
  restart the table is rescanned so the row reflects what came back.

## 6. Reserved CPU Sets

The Check All / Uncheck All / Invert command bar is gone (cores are ticked one
by one; each tick still writes the kernel reservation immediately). The
"Apply at Startup" toggle stays, because older builds drop the reservation on
every reboot.

## 7. Verification

```bash
dotnet build kaliteConfig.csproj -p:Platform=x64        # 0 errors
dotnet test kaliteconfig.Tests/kaliteConfig.Tests.csproj # 116 passed
dotnet run --project kaliteConfig/tools/ThreadVerify     # 7 passed, 0 failed
dotnet run --project kaliteConfig/tools/BoostVerify      # 11 passed, 1 known failure
```

`BoostVerify`'s single failure (`background priority class == BelowNormal: read
Normal`) is pre-existing in `BackgroundThrottleService`'s process-priority path
and is documented in `Docs/ThreadTuning.md`.

`%LOCALAPPDATA%\kaliteConfig\rules-debug.log` is the place to confirm the DWM
rule live: after this change the keeper logs
`apply-now: pattern=[dwm.exe] pids=[…]` instead of the old
`pattern=[Input/Sensor Threads] pids=[]`.

Not exercised headlessly: the boost tick box column, the NVIDIA picker dialog and
the affinity dialogs are WinUI surfaces — they need a manual pass.
