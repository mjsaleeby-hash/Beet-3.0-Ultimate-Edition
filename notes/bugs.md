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
