# Thread tuning — UI changes and the ideal-processor fix

Scope: `Controls/ThreadListDialog.xaml(.cs)`, `Services/ThreadTuningService.cs`,
`Native/NativeMethods.Affinity.cs`, `Pages/ThreadTunerPage.xaml(.cs)`.

## 1. Removed from the UI

**Threads dialog (`ThreadListDialog`)**

- The **Memory priority** dropdown (Very low … Normal (5)) is gone, along with its
  label, `MemCombo_SelectionChanged`, the editor-load read that populated it, and the
  line that captured the live value into a saved rule. `TunerThreadRule.MemoryPriority`
  and the applier in `ProfileWatcherService` are untouched, so rules saved earlier
  still apply what they always did — the dialog simply no longer offers the option.

**Threads page (`ThreadTunerPage`)**

- Four suspend-mode controls were removed: **Resume all**, the **Auto** tick box,
  **Add game** and **Games…**, together with their handlers (`Page_ResumeAll`,
  `SuspendAuto_Toggled`, `Page_AddGame`, `Page_ManageGames`) and the now-dead
  references in `RefreshSuspendUi`.

  Note what this means for suspend mode: the four controls *were* its whole UI.
  `ForegroundSuspendService` still runs, still reports its status and suspended count
  on the page (`PageSuspendStatusText`), and `MainWindow` still resumes everything it
  suspended on exit, but nothing in the UI can now toggle `Auto`, resume early, or
  edit the game list. If suspend mode is meant to keep working, one of those controls
  needs to come back somewhere.

## 2. "Apply ideal processor" did nothing — and said it worked

### What was wrong

`SetIdealProcessorAsync` ignored the BOOL returned by `SetThreadIdealProcessorEx`:

```csharp
NativeMethods.Affinity.SetThreadIdealProcessorEx(thread, ref proc, IntPtr.Zero);
// Ignore failures silently (avoid UI toaster popups for protected threads)
```

The dialog then wrote `Current: group 0 CPU 10.` and `Ideal processor set to CPU 10.`
unconditionally, so a rejected write looked exactly like a successful one.

The read path was broken the same way. `GetThreadIdealProcessorEx` was declared as
returning the processor number with "(DWORD)-1 on failure"; it actually returns
**BOOL** and fills the `PPROCESSOR_NUMBER` out parameter, so `number == uint.MaxValue`
was never true and a failed read reported an uninitialised processor as the thread's
live ideal processor (and the editor silently pre-ticked box 0).

### Evidence

`tools/ideal-processor-probe.ps1` (added) calls the real kernel32 exports:

| what | result |
| --- | --- |
| `GetThreadIdealProcessorEx` return | `1` (BOOL TRUE); the number comes from the out struct |
| set ideal 7, then read back | `ok=True`, reads back `G0:7` |
| set ideal 255 (no such CPU) | `ok=False`, error **87** (`ERROR_INVALID_PARAMETER`) |
| narrow affinity to CPUs 0-3, set ideal 10 | `ok=False`, error **31** (`ERROR_GEN_FAILURE`) |
| CPUs accepted on this machine (16 logical) | **0-9 accepted, 10-15 refused with error 31** |

Why 10-15 are refused here:

```
HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\kernel : ReservedCpusets = 0xFC00
```

CPUs 10-15 are held by the CPU-set partition reservation (the gaming partition this
app sets up). Only threads *inside* that partition may use them. The screenshot that
reported this bug had **CPU 10** ticked — a CPU Windows refuses for an ordinary thread.

### What changed

- `Native/NativeMethods.Affinity.cs`: `GetThreadIdealProcessorEx` is declared as
  `bool` with `[return: MarshalAs(UnmanagedType.Bool)]`; the stale "returns the
  processor number" comment is replaced with what the API actually does.
- `ThreadTuningService.GetIdealProcessorAsync`: checks the BOOL and throws
  `CpuSetService.Friendly(...)` when the read failed.
- `ThreadTuningService.SetIdealProcessorAsync`: checks the BOOL, **reads the value
  back** (the same discipline `SetAffinityAsync` already used), and explains a
  rejection:
  - CPU outside the thread's affinity mask → names the CPUs that are usable and says
    to widen affinity;
  - CPU inside the mask but reserved by the CPU-set partition → says so, and lists
    the usable CPUs;
  - error 87 → no such processor in that group;
  - error 5 → protected thread.
- `ThreadListDialog`: a failed read no longer pre-selects a box (`Current: unreadable —
  pick a CPU below.`), and after a successful apply the label shows what the kernel
  reports rather than what was requested.
- `ProcessOptimizer/Services/ProcessStateSnapshotService.cs` and
  `tools/BoostVerify/Program.cs`: both callers updated for the BOOL return (the
  snapshot no longer records a bogus ideal processor).

## 3. "Make permanent" applies immediately

The dialog's **Make permanent (save as rule)** button saved a rule and stopped
there: rules were only applied when the process was next *launched* (or when
"Apply now" was pressed in the Rules tab), so a thread tuned by hand looked like
it kept resetting. Saving now finishes the job with
`ProfileWatcherService.ApplyToProcessAsync(profile, pid)` against the process you
are looking at, and the status line reports how many threads it touched.

## 4. Verification

```bash
# what the kernel really does (no app needed)
pwsh -File tools/ideal-processor-probe.ps1

# real ThreadTuningService against a real thread of a child process
dotnet run --project tools/ThreadVerify      # 7 checks, 0 failures
```

`tools/ThreadVerify` (added) runs the real service: it reads the ideal processor,
applies a CPU inside the mask and confirms the kernel reports it back, narrows the
thread's affinity and confirms the impossible CPU is **reported** (it used to be
swallowed), confirms the failed write left the live value untouched, finds at least
one CPU that applies end to end, and confirms a reserved CPU (10, mask `0xFC00`) is
refused *with the reservation explained*.

Also green: `dotnet build kaliteConfig.csproj -c Debug -p:Platform=x64` (0 errors) and
107 unit tests. `tools/BoostVerify` passes 11 of 12 checks; the one failure
(`background priority class == BelowNormal: read Normal`) is in
`BackgroundThrottleService`'s process-priority path, which this change never touches.
Its boost checks do cover the mechanism the remaining boost controls use:
"background boost disabled: thread reports disabled=True" is the process-level flag
plus a thread reading back `GetThreadPriorityBoost`, i.e. exactly the pair
`BoostPreferenceService`'s suppression path writes.

Not exercised headlessly: the toolbar and dialog paths, which live in WinUI
page/dialog code. "Make permanent" reports how many live threads it touched in its
status line, which is where to check it by hand.
