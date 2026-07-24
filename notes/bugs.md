## 2026-07-24 — Scheduled "Users" backup ran twice, leaving a phantom "Failed" entry

**Status: DIAGNOSED — fix proposed, not applied** (surfaced by the cohort report's
"candidate 1 failed run / 17"; investigated read-only, no deploy)

**Symptom:** The 4.0-candidate cohort showed one failed backup run. The `backup_log.json`
entry (`afd04ae9`, job "Users", 2026-07-17 11:15:07) read `Status=Failed, FilesCopied=0`,
`Message="Interrupted — the application closed while this job was running."` A backup that
looked like it silently died mid-run.

**What actually happened (the opposite of a failure):** the Users job *succeeded* on 7/17
— it just ran TWICE. `operational.log` on one clock:

```
11:15:01.104  === Headless run: job 16e1a3e1 ===         (Windows Task Scheduler --run-job)
11:15:01.196  Scheduled job started: 'Users'
11:15:55.770  Scheduled job completed: 'Users' — 376 copied, 89918 skipped, 11 deleted
11:15:55.798  === Headless run complete: ran ===          (RunHeadlessJob's post-run log)
11:15:55.939  Scheduled job started: 'Users'              (SECOND run — NO "Headless run" marker)
11:16:41.630  Scheduled job completed: 'Users' — 2 copied, 90292 skipped
```

Two `BackupCompleted` telemetry events confirm two full runs. The second run re-walked all
~90k files (~45 s of wasted I/O) and copied the 2 files the first run had just missed.

**Root cause — two schedulers firing the same job.** Feature F runs backups when the app is
closed via a Windows Task Scheduler `--run-job` task; when the app is *open*, its in-process
minute-ticker (`SchedulerService.RunAsync`) also fires due jobs. Both fired Users at 11:15.
The second run has no `=== Headless run: job ===` marker, so it came from a *different
process* — a foreground Beet that started 2026-07-16 16:43:55 (the restart after the shutdown
crash below) and was still open. The per-job cross-process `JobMutex` is `TryAcquire` —
non-blocking, skip-if-busy (`SchedulerService.cs:488`). That prevents *overlapping* double
runs, but the two fired ~1 minute apart and did NOT overlap: headless took the mutex, ran,
released it at 11:15:55.8, and the foreground ticker then acquired it 141 ms later and ran
again. The foreground process acted on a **stale `NextRun`** — headless had advanced it in
`jobs.json`, but the foreground's in-memory `_jobs` copy hadn't reconciled via its file
watcher yet, so it still believed Users was due.

The phantom "Failed" entry is a second-order effect: with both processes mutating the shared
`backup_log.json`, one process's `Running` placeholder was left stranded (its completion lost
to the cross-process write/merge window), and a later launch's `ReloadFromDisk` housekeeping
(`BackupLogService.cs:147-153`) flipped any pre-process-start `Running` entry to
`Failed`/"Interrupted." So a successful run is reported as a failure.

**Scope:** one-off. Across all 16 scheduled fires in the 8-day window, only this one
double-ran; every other fire logged exactly one run. It requires a foreground instance to be
open at the same minute Task Scheduler fires the same job — uncommon, but it corrupts the
error metric and shows the user a red "Failed" for a backup that worked.

**Diagnosis method:** reconciled `backup_log.json` (local), telemetry JSONL (UTC → local; the
UTC/local offset nearly mis-set the whole timeline), the Windows event log (VSS), and
`operational.log` onto one clock; then counted runs per fire with a `grep` of the "Headless
run"/"Scheduled job started|completed" markers across the window.

**Proposed fix (needs characterization tests first — touches the proven scheduler/log core,
Wave-3 territory per the plan):**
- Make the in-process ticker defer to the Windows task: when a job has a registered
  `--run-job` task, the foreground scheduler should not *also* execute it (or should
  re-check `NextRun` from disk immediately before dispatch, under the JobMutex, so a
  just-advanced `NextRun` is seen).
- Alternatively, have `ExecuteJobAsync` re-read the authoritative `NextRun` after acquiring
  the JobMutex and bail if it has already advanced past this fire — closes the stale-snapshot
  window regardless of which two schedulers race.
- Separately, the reconciliation should not label a run "Failed/Interrupted" when a
  *completed* entry for the same JobId + fire exists — that would stop the phantom even if a
  double-run slips through.

**Systemic risk:** the same stale-in-memory-`NextRun` window exists for the manual "Back up
now" button racing a scheduled fire. Any fix should target the shared dispatch gate, not just
the ticker.

---

## 2026-07-24 — Benign shutdown crash: DllNotFoundException in mixed-mode VSS teardown

**Status: DIAGNOSED — fix proposed, not applied**

**Symptom:** One genuine `BeetsBackup.exe` 4.0.0.0 crash in the candidate window
(2026-07-16 16:38:35), logged by the OS as Application Error + .NET Runtime + WER (exception
`0xe0434352`, faulting module KERNELBASE.dll). `crash_dump.log` records
`System.DllNotFoundException: "Dll was not found."` with the stack:

```
at __std_type_info_destroy_list(__type_info_node*)
at __scrt_uninitialize_type_info()
at _app_exit_callback()
at <CrtImplementationDetails>.ModuleUninitializer.SingletonDomainUnload(...)
```

**Root cause:** a *shutdown-teardown race*, not a runtime failure. `operational.log` shows the
crash fired AFTER "═══ Application shutting down ═══" — all backup work was already complete
(uptime 2h38m). The `<CrtImplementationDetails>.ModuleUninitializer` frame is C++/CLI
(mixed-mode) module cleanup: during AppDomain unload, a native satellite of the VSS interop
(AlphaVSS-style `.Native.x64.dll`, loaded on demand for the shadow-copy fallback) is
uninitialized, and the CRT's type_info teardown tries to touch a DLL the exiting process can
no longer resolve → `DllNotFoundException`. The same "Error disposing services:
AggregateException (A task was canceled)" appears on the *clean* 13:41 shutdown that same day,
so the dispose-cancellation is routine; only occasionally does the mixed-mode uninitializer
lose the teardown-ordering race and escalate to a FATAL `AppDomain.UnhandledException`.

**Impact:** cosmetic but not free. No data at risk and no lost work (everything completed).
But each occurrence (a) writes a crash dump, (b) records an OS Application Error that inflates
crash telemetry, and (c) makes the next launch believe the previous session ended uncleanly
(`DiagnosticsService.ConsumeUncleanShutdown`), which trips the crash banner. Note the current
`RunHeadlessJob` fast-exits via `Environment.Exit` (skipping most managed teardown) while the
foreground `OnExit` path lets the CLR run a full managed AppDomain unload — which is why this
surfaces on the foreground exit, not the headless one.

**Diagnosis method:** matched the WER/Application-Error records to `crash_dump.log` and to
`operational.log`; the "shutting down" line immediately preceding the FATAL, plus the
identical benign dispose-cancellation on a clean exit, establishes teardown-ordering rather
than a live fault.

**Proposed fix (low-risk, does NOT touch the VSS "leave alone" core):**
- In the shutdown path, call `DiagnosticsService.MarkExitedCleanly()` *before* the managed
  AppDomain teardown that can throw (headless already marks clean in its `finally`; verify the
  foreground `OnExit` marks clean before returning), so a benign teardown throw doesn't trip
  the unclean-shutdown banner.
- In `OnDomainUnhandledException`, treat a `DllNotFoundException` whose stack is in
  `ModuleUninitializer`/CRT teardown *while already shutting down* as WARN, not FATAL — do not
  write a crash dump for it. Guard narrowly (shutdown flag + exception type + frame) so real
  faults are unaffected.
- Optional hardening: fast-exit the foreground path (`Environment.Exit`) once dispose
  completes, mirroring headless, so the CLR never runs the mixed-mode uninitializer at all.

**Systemic risk:** any future mixed-mode/native dependency inherits this teardown hazard; the
robust guard is the "already shutting down → downgrade + no dump" handler, which is
dependency-agnostic.

---

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
