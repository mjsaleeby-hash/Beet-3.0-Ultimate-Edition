## 2026-07-16 — PerfMon collector crashed 7x in 8 days on a stale binary

**Status: FIXED** (this session)

**Symptom:** The Windows Application log showed `BeetsBackup.PerfMon.exe` faulting
repeatedly — 7 times between 07-08 and 07-15 (07/8, 07/12, 07/13, 07/14, and 3x on
07/15), across two distinct fault buckets. Meanwhile `BeetsBackup.exe` itself crashed
ZERO times in the same window. The collector gathering the 3.0→4.0 performance data was
the only thing falling over, punching holes in the very dataset it existed to produce.

**Root cause:** The fix was written, merged, and never compiled. Commit `536f58d`
(2026-07-06, "Harden PerfMon against session-log collision and process-handle denial")
landed on `main`, but the `BeetPerfMon` scheduled task executes
`PerformanceMonitor\bin\Release\net8.0-windows\win-x64\BeetsBackup.PerfMon.exe`
**directly out of bin** — there is no publish step and no `CopyExeToRoot` equivalent for
this project, unlike the main app. So merging changed nothing on disk: the task kept
launching the binary built on 2026-06-22 from `b325dbe`, which still contained both
original crash bugs (`SessionLogWriter` `FileMode.CreateNew` filename collision, and
unguarded `_process.Handle` → Win32 access-denied).

The 2026-07-06 session diary had explicitly flagged "NOT deployed — needs stop+rebuild+
restart, left to user"; it was then lost in the branch-merge session two days later.

**Diagnosis method:** Compared the deployed exe's `LastWriteTime` (2026-06-22 14:53)
against the fix commit's date (2026-07-06) — `git log -- PerformanceMonitor/`. An
8-week-old binary for a 10-day-old fix is the whole story. Confirmed the task's target
path via `(Get-ScheduledTask -TaskName BeetPerfMon).Actions`.

**Fix:** `Stop-ScheduledTask` → `dotnet build PerformanceMonitor\BeetsBackup.PerfMon.csproj
-c Release` (0W/0E) → `Start-ScheduledTask`. Verified the new exe+dll are dated today,
that the hardening is present in the compiled source, that PerfMon reattached (the session
log filename embeds the monitored Beet PID), and that samples are flowing (+12 KB/20 s).

**Underlying systemic risk — this is the real lesson.** "Merged" and "deployed" are
different states, and this repo makes it easy to confuse them because the two projects
deploy differently: the main app auto-copies to `Documents\BeetsBackup.exe` via the
`CopyExeToRoot` target (`AfterTargets="Publish"`), while PerfMon has no such step and
runs from `bin` forever. **A second instance of the same class of bug was found the same
day:** Wave 1 was merged and deployed on 07-08 but its `BuildTag` was never flipped, so
8 days of telemetry recorded Wave-1 code under the `3.0-baseline` label. Both sat
correct-in-git and wrong-on-disk for over a week.

**Guard:** before trusting collector data, check the bin exe's `LastWriteTime` against the
date of the last commit touching that project. See `improvements/IMPLEMENTATION-PLAN.txt`
§9 and the deploy notes in the project memory.

---

## 2026-04-18 — Zombie process after scheduled headless run

**Status: FIXED** (this session)

**Symptom:** After Windows Task Scheduler runs `BeetsBackup.exe --run-job <guid>`, the process sometimes lives indefinitely — no window, no tray icon. Recurring issue; a prior `Environment.Exit` fix (commit `48f0e80`) was placed after code that could deadlock, so it never executed.

**Root cause:** Sync-over-async deadlock on the WPF dispatcher. `App.RunHeadlessJob` called `scheduler.RunJobByIdAsync(jobId).GetAwaiter().GetResult()` on the UI thread inside `OnStartup`. Every `await` in the async chain captured `DispatcherSynchronizationContext`. When the backup finished, continuations tried to post back to the dispatcher — which was frozen in `GetResult()`. The Task never completed, so `Environment.Exit` in the `finally` block was never reached.

**Diagnosis method:** `dotnet-stack <PID>` on a live zombie showed the UI thread at `TaskAwaiter<Boolean>.GetResult()` → `App.RunHeadlessJob` → `App.OnStartup`; thread-pool workers were idle (work was already done).

**Fix (App.xaml.cs):**
1. Wrap the scheduler call in `Task.Run(...)` so the async state machine runs on the thread pool, not the dispatcher thread — no sync context captured.
2. Bound `Services.Dispose()` with a 5-second `Task.Run(...).Wait(5s)` timeout and log if it expires.
3. Added `ArmShutdownWatchdog(TimeSpan, string)` — background thread calls `Process.GetCurrentProcess().Kill()` after timeout. Armed at 30s in `RunHeadlessJob`, 15s in `OnExit`. Hard backstop so no code path can produce a zombie.

**Underlying systemic risk:** Zero `ConfigureAwait(false)` calls anywhere in `Services/`. Any `.GetAwaiter().GetResult()` call from a thread with a captured `SynchronizationContext` can reproduce this class of deadlock. Phase 4 work will sweep `ConfigureAwait(false)` across all service code to eliminate the risk at the source.
