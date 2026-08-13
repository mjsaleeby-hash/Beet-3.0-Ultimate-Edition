## 2026-08-13 — Wave 2.5: the legend is the accessible surface, not decoration

**Decision: treat the legend as the chart's accessible representation rather than as a caption
beside it — and accept two deliberate behaviour changes to get there.** Design decisions were
made 2026-08-05, implementation ran 08-05/06, sign-off landed 08-13.

The donut distinguishes up to 11 slices **by colour alone**. That makes the legend the required
non-colour fallback, not decoration — so it has to be reachable, operable and announceable, which
it was not. Legend rows were `Cursor="Hand"` Borders with mouse handlers: they advertised
interactivity while being mouse-only. A Border inside an `ItemsControl` takes no focus, has no
keyboard path, and exposes no automation name. Rows became chrome-free `Button`s, which are
natively focusable, activate on Space/Enter, and expose the **Invoke** pattern — the pattern that
actually matches what a row does (navigate into that folder). Single-click behaviour is
byte-identical, so R6 holds for the mouse path.

**Two stated R6 exceptions, both signed off on the mockup before any XAML was written:**

- **Wedges between 0.028% and 0.05% stop rendering.** There were two disagreeing thresholds — a
  0.1-degree sweep guard in the control and 0.05% on the model — so slices in that band got a
  legend row with no wedge behind it. One `MinVisiblePercentage` now feeds both, and a theory pins
  `IsRenderable == !IsNegligible` so they cannot drift apart again.
- **Dark-mode percentages changed colour** despite passing contrast today (4.51–6.41). Nobody
  complained about them; the change was made anyway because keeping the slice colour in light mode
  was untenable and two divergent rules for one TextBlock is worse than one uniform rule.

**Contrast is why the percentage stopped being slice-coloured.** All 11 palette colours measured
2.45–3.49 against light's `#FFFFFF` — every one fails WCAG AA's 4.5:1 for text. The colour
association is already carried by the swatch in column 0, so painting the number too bought
nothing and cost readability. Now `PrimaryTextBrush` in both themes.

**Light-only donut change; dark deliberately untouched.** `DonutCenterColor` went `#F0F0ED` →
`#FFFFFF` in `Themes/Light.xaml`. `Themes/Dark.xaml` was **not modified anywhere in this wave**
(R4): dark's `#121735` equals its `PanelColor` and reads as an intentional inset well. Only light
had the reported grey-beige smudge sitting on a white card.

**Task 7's automation-tree question was left open by the spec and answered by measurement, not
assertion.** The container was named first; suppression of child peers was gated behind an actual
inspection. The note that matters for anyone revisiting it: do **not** suppress by overriding
`OnCreateAutomationPeer` on the UserControl and filtering children by peer type —
`ButtonAutomationPeer` derives from `UIElementAutomationPeer`, so that filter strips the legend
buttons out of the tree and destroys the exact thing this wave built.

**Deliberate duplication, queued as debt.** `LegendRowFocusVisual` duplicates `AccentFocusVisual`,
which is stranded in `MainWindow.xaml`'s `Window.Resources` and unreachable from a UserControl.
Promoting it to `Themes/Controls.xaml` is Wave 2.6's job — moving a resource out of MainWindow was
not this wave's risk to take.

**A whole-branch review caught three real defects after the tasks were "done"**, all in the new
interaction code: a highlight desync between the three independent enter/leave sources, stale
hover/focus indices surviving a chart rebuild, and rows wired to *logical* focus
(`GotFocus`/`LostFocus`) rather than keyboard focus — which a mouse click also grants and which
survives window deactivation, so rows stayed lit forever with no focus ring. All three were
reachable by ordinary use and none were caught by the 230-test suite, because they live in WPF
input routing that the suite does not cover.

**Process finding, recorded because it nearly cost the sign-off.** The first reported manual pass
came back clean — against the wrong binary. The user tested through the pinned shortcut, which
still pointed at `4.0.0+9c272ed` from 08-04, a build predating every change above. There was no
focus ring, no legend `Button` and no white donut hole in it to fail. The pass was discarded, the
wave deployed (`4.0.0+9bc26d4`, verified byte-identical to the publish output by SHA256), and the
pass redone against the real thing. **A clean manual result is only evidence if you know which
binary produced it** — pin the version before recording an R7 pass. This is the same
premise-before-conclusion failure as the three advisory-report items that turned out to rest on
unchecked assumptions.

---

## 2026-08-04 — Wave 2.4 shipped, and the metric that verifies it was broken

**Decision: implement 2.4 as planned, but also fix the telemetry field it is measured by —
because as written, the measurement could not see the thing 2.4 changes.**

The change itself is exactly the plan: `CalculateFolderSizesWithProgressAsync` takes
`MaxDegreeOfParallelism` from `DriveTypeService` instead of `Environment.ProcessorCount`, so a
spinning disk sizes with one walker instead of 8–16 thrashing the head. No size cache (still
"DOUBTED" in plan §2). Two deliberate deviations:

- **Added `GetReadWorkerCount(path)` instead of calling `GetWorkerCount(path, path)`.** Sizing
  walks one drive, not a source→dest pair. The new method delegates to the same `ComputeWorkers`
  table with that drive on both sides — identical decision, honest call site. Repackaging the
  mechanism, not rewriting it (R5).
- **Sized off the pane's current path, not `directories[0]`.** An empty or all-files pane has no
  directory to classify from, and the existing telemetry already had that latent hole.

**The metric was measuring the wrong thing.** `FolderSizeCompleted` reported
`DriveInfo.DriveType`, which returns `Fixed` for **both SSD and HDD**. Verification spec §6 lists
2.4's evidence as "FolderSizeCompleted ms HDD" — but no field in the data could separate an HDD
run from an SSD one, so that row was unmeasurable from the day it was written. It now reports the
`DriveKind` the fan-out decision is actually made on, plus the `workerCount` used so a report can
confirm HDDs really serialized rather than inferring it from a label.

Safe downstream: the sink serializes payloads **by name**, so the extra field is additive, and
`PerformanceMonitor/Analysis/CohortReport.cs` reads only `directoryCount` and `elapsedMs`. The
value change to `driveType` (`Fixed` → `SSD`/`HDD`) is a break in historical comparability, which
is why it lands **now** — the 4.0-candidate cohort closed 7/24 and no window is open.

**Expect no local speed change.** This machine classifies `C:\` as SSD, so its fan-out is
unchanged by design. The win is on spinning and removable media; claiming a local improvement
would be the same unverified-premise error that killed 2.3.

---

## 2026-08-04 — Wave 2.3 (live-filter debounce) dropped as verified-N/A

**Decision: drop Wave 2.3 entirely rather than implement it. The optimization targets a
property nothing writes to.**

- `MainViewModel.SearchFilter` has **no writer anywhere in the product**. A full-repo grep
  returns only the property definition (`ViewModels/MainViewModel.cs:90`), its own
  doc-comment (`:598`), and three advisory reports in `improvements/` that assume it is live.
- The pane search boxes bind `DeepSearchQuery` (`Views/MainWindow.xaml:1345`,
  `Views/SplitPaneWindow.xaml:312`) — that is the deep-search path, which populates pane
  results. It is a different mechanism and does not flow through this filter.
- Consequence: the predicate's `if (string.IsNullOrEmpty(_searchFilter)) return true` guard
  short-circuits on **every** call. A 150 ms debounce would coalesce keystrokes on a property
  that receives none — a measurable nothing.
- **How the item got planned:** the performance report reached P5 by reading the setter and
  correctly noting it calls `Refresh()` on two `ICollectionView`s per assignment. It then
  assumed keystrokes flowed in. Nobody checked for a writer. Same failure mode as the FAT
  re-copy assumption — a finding that *looks* right is not true until checked.
- **What is NOT dead:** `CreateFilteredView` and both `ICollectionView` properties are live —
  the panes bind them as `ItemsSource`. Only the always-empty filter *string* is dead, so the
  views must stay. Retiring the unused property + unreachable predicate branch is folded into
  Wave 2.6 (de-dup/cleanup), where it belongs with the other behaviour-preserving tidying.
- **Why not wire up a real filter box instead:** that would be a new feature, outside Wave 2's
  behaviour-neutral scope. If it is ever built, reopen 2.3 then — and re-check for a writer
  before trusting §FIX F again.

**Also recorded this session:** the Wave 2.2 R7 manual pass PASSED (big-folder navigation,
deep search, selection-dependent commands), and the `v4.0.0` version label was confirmed
rendering. That binding fails silently, so a blank label would have been the only tell. Both
verifications had been owed since 7/24. Wave 2.2's deploy state was also corrected — it shipped
7/29 riding `72f3364`, not "pending" as the plan had said.

---

## 2026-07-24 — Field-defect triage from the cohort report (shutdown crash + double-run)

### Shutdown crash: hard-terminate, not suppress-the-dump (fix applied)

**Decision: end the foreground process with `TerminateProcess` after flushing, instead of only
silencing the crash dump.**

- The crash is a benign mixed-mode VSS-interop teardown race (`DllNotFoundException` from the
  C++/CLI module uninitializer during `Environment.Exit`'s AppDomain unload; full root cause in
  `notes/bugs.md`). All work has finished by then; it does NOT trip the unclean-shutdown banner
  (`MarkExitedCleanly` runs first). Harm is a misleading FATAL crash dump + an OS Application
  Error that inflates the cohort crash count.
- **Why not just suppress our crash dump:** that leaves the exception escaping the process, so
  Windows still logs the Application Error and the cohort still counts a crash. It would hide the
  symptom from our own log while changing nothing the verification system sees. Skipping the
  teardown callbacks (TerminateProcess runs no CRT/module uninitializers) is what actually
  prevents the OS-level fault.
- **Why it's safe to skip graceful teardown here:** services + telemetry are already disposed,
  the clean-exit sentinel is cleared, terminal backup-log statuses are written inline and never
  debounced (`BackupLogService.SaveNow`), and the operational-log queue is flushed explicitly
  first. The only droppable state is a transient progress %. A second guard (`_isShuttingDown` →
  log WARN, no dump in `OnDomainUnhandledException`) covers the headless `Environment.Exit` route,
  which keeps `Environment.Exit` because it must return an exit code to Task Scheduler.

### Double-run / phantom "Failed": DEFERRED, not fixed now

**Decision: do NOT patch the 7/17 scheduled-job double-run this pass; leave it DIAGNOSED.**

- Every safe-looking quick fix (suppress the "Interrupted" relabel; skip a job whose `NextRun`
  already advanced) risks a FALSE NEGATIVE — hiding a genuinely interrupted backup. For a backup
  tool, silently not reporting a real failure is worse than a phantom failure that's explained.
- The correct fix (stop two schedulers from both executing a job) touches the proven
  scheduler-dispatch + cross-process log-reconciliation core, which the master plan gates behind
  characterization tests (Section 7 entry bar; R5 "leave alone"). Rushing it in is exactly the
  move that plan warns against. Recorded in `notes/bugs.md` with a proposed fix for when the
  characterization suite exists.

### Correction: the headless path no longer arms a shutdown watchdog

- The 2026-04-18 entry below and the old `bugs.md` zombie note say the watchdog is "Armed at 30s
  in `RunHeadlessJob`, 15s in `OnExit`." Current code (`App.xaml.cs`) arms it ONLY in `OnExit`,
  at 10s (`ShutdownWatchdogTimeout`); `RunHeadlessJob` relies on its bounded 5s dispose +
  `Environment.Exit` with no watchdog. So a *hung* headless job today has no hard backstop — a
  latent gap, noted here so it isn't rediscovered from scratch. Not fixed this pass (out of the
  crash's scope); candidate for the same scheduler-hardening work as the double-run.

---

## 2026-07-16 — 4.0-candidate tagging, and Wave 2.1 theme tokens

### Version bumped 3.0.0 → 4.0.0 alongside the BuildTag flip (commit `e75accc`)

**Decision: flip `BuildTag` to `4.0-candidate` AND bump the version block, not just the tag.**

- The `BuildTag` alone drives the cohort split — `BeetDataPaths.Resolve` prefers the tag
  stamped on telemetry. But it *falls back* to `DeriveTagFromVersion` (major >= 4 →
  candidate) when telemetry is unavailable. Left at 3.0.0, that fallback would have
  reported this build as `3.0-baseline` — contradicting its own tag, and reproducing the
  exact mislabeling we were fixing.
- Cost accepted: the app reports itself as 4.0.0 while Wave 2 is still in progress. That's
  what "candidate" means; it isn't shipping to anyone.
- **By-product:** the hard-coded `Text="v3.0.0"` label in `MainWindow.xaml` is now stale.
  Deliberately NOT fixed in the same commit (rule R2, one logical change per commit) —
  logged as a follow-up in `improvements/IMPLEMENTATION-PLAN.txt` §9.

### Wave 2.1: a new `EmphasisTextColor` token rather than reusing `PrimaryTextBrush`

**Decision: literal `White` maps to a NEW token (dark `#FFFFFF`, light `#111111`), not to
the existing `PrimaryTextBrush`.**

- `PrimaryTextBrush`'s dark value is `#E8E8EC`, not `#FFFFFF`. Pointing the old `White`
  literals at it would have shifted dark mode — breaching plan rule R4 ("every new dark
  token value must equal the exact current hex so dark renders byte-identical"). The
  delta is imperceptible, but R4 exists precisely because the dark theme is currently good
  and must not drift; bending it silently is how drift starts.
- R4 was verified **mechanically** (scripted each of the 17 new dark values against the
  literal it replaced), not by eye. Recommend the same for any future theme work.

### Wave 2.1 scope: named colours are in, brand decoration is out

**Decision: `White` literals are in scope; the rainbow gradient and title glow are not.**

- The `uxui-assessment` C1 audit grepped only for `#hex` and therefore missed literal
  `White` — which was the more damaging bug. The app title was `White` on the light
  toolbar (`#F4F4F9`) and the card action rows `White` on `#F7F7FC`: both already
  invisible in light mode before this change. **De-hardcoding only the hex would have made
  light mode worse**, not better — a newly-pale status banner still carrying `White` body
  text is white-on-pale-pink. So "restore the light theme" required the named colours too.
- **Generalises:** when an audit doc enumerates literals, re-grep for named colours. The
  doc's list is a starting point, not an inventory.
- Three `White` literals are deliberately KEPT and commented in place: the check on the
  green shield, the `!` on the red triangle, and the toggle knob. Each sits ON a saturated
  fill that stays saturated in both themes, so they must not follow the theme.
- The rainbow gradient (`#4CC2FF→#FF6B8A→#4ade80`) and the title's violet glow (`#9F7CFF`)
  remain literal. They are saturated brand decoration that reads correctly on both themes —
  re-theming them is a design decision, not a mechanical de-hardcoding, and it is the
  user's call to make.

---

## 2026-04-18 — Performance Phase 1 & Zombie Fix

### Server GC enabled
- Added `<ServerGarbageCollection>true</ServerGarbageCollection>` and `<ConcurrentGarbageCollection>true</ConcurrentGarbageCollection>` to `BeetsBackup.csproj`.
- **Rationale:** Default workstation GC serializes collection with the calling thread. Server GC allocates one heap per logical core (8 on this host) and collects in parallel — roughly halves GC pause time for a long-running process that churns large byte buffers.

### ArrayPool buffers — 1 MB pooled, not per-file heap allocations
- `FileSystemService.CopyFileWithHash` and `TransferService.ThrottledCopy` now rent from `ArrayPool<byte>.Shared` and return in `finally`.
- Buffer size raised to 1 MB in both paths (was 80 KB and 64 KB respectively).
- **Rationale:** Per-file `new byte[N]` churns Gen 0 at scale (800 MB of allocations across a 10,000-file backup). Pooling eliminates that entirely.

### PowerManagement helper — prevent sleep during transfers
- New `Services/PowerManagement.cs` wraps `SetThreadExecutionState` via PInvoke.
- `using var _awake = PowerManagement.KeepSystemAwake();` applied in `CopyAsync`, `CompressAsync`, `ExtractAsync`, `MoveAsync`.
- **Rationale:** Without this, a long overnight backup on an unattended machine will silently fail when Windows hits the sleep timeout.

### Cancellation-responsive throttle delay
- `TransferService.ThrottledCopy` replaced `Thread.Sleep(delay)` with `ct.WaitHandle.WaitOne(delay)`.
- **Rationale:** `Thread.Sleep` ignores cancellation. A user hitting Cancel during a throttled transfer was stuck waiting up to the full delay interval.

### RegisterWaitForSingleObject — no polling thread
- `App.xaml.cs` `StartShowSignalListener` replaced the `Task.Run` + 1-second `WaitOne` polling loop with `ThreadPool.RegisterWaitForSingleObject`.
- Removed the now-unnecessary `_showSignalCts` field.
- **Rationale:** The polling loop held a thread-pool worker permanently blocked, waking every second. OS-level callback has zero thread cost until the signal fires.

### PerformanceMonitor excluded from main build
- Added `PerformanceMonitor\**` to `DefaultItemExcludes` in `BeetsBackup.csproj`.
- **Rationale:** The monitor is a standalone console tool; it was being pulled into the main build inadvertently.

---

### Zombie process — sync-over-async deadlock (root cause + fix)

**Decision: headless job path must not block the UI thread on an async call.**

- **Root cause:** `App.RunHeadlessJob` called `scheduler.RunJobByIdAsync(jobId).GetAwaiter().GetResult()` on the WPF UI thread during `OnStartup`. Awaits inside the async chain captured `DispatcherSynchronizationContext`. On completion, continuations posted back to the dispatcher — which was blocked in `GetResult()`. Deadlock: Task never completed, `Environment.Exit` in `finally` never executed, process lived forever with no window.
- **Evidence:** `dotnet-stack` dump on a zombie PID showed UI thread frozen at `TaskAwaiter<Boolean>.GetResult()` → `App.RunHeadlessJob` → `App.OnStartup`, with idle thread-pool workers (backup was done).

**Fixes applied to `App.xaml.cs`:**
1. **Task.Run wrapper:** `Task.Run(() => scheduler.RunJobByIdAsync(jobId)).GetAwaiter().GetResult()` — async state machine runs on thread pool, no dispatcher context captured.
2. **Bounded dispose:** `Services.Dispose()` in headless `finally` runs via `Task.Run(...).Wait(5s)` with a timeout log.
3. **Shutdown watchdog:** New `ArmShutdownWatchdog(TimeSpan, string)` spawns a background thread that calls `Process.GetCurrentProcess().Kill()` after the timeout. Armed at 30s in `RunHeadlessJob`, 15s in `OnExit`. Guarantees termination even if `Environment.Exit` itself hangs.

**Note:** Phase 4's `ConfigureAwait(false)` sweep across `Services/` would eliminate this entire class of deadlock at the source.

---

## 2026-04-29 — Performance Phase 3 plan: parallel copy engine

### Goal
Fan out file copies across worker tasks bounded by drive-type-aware concurrency. Target: 2–5×
throughput on SSD-to-SSD, no regression on HDD where parallelism is counter-productive.

### Architecture: enumerate-then-copy (two-pass), not recursive parallel

The current `CopyItem` is depth-first recursive — directories enumerate children and call
`CopyItem` on each. Parallelizing that would require parallel enumeration with shared mutable
state at each tree level, plus careful ordering to ensure parent dirs exist before child files
land. Easier to do it as two passes:

1. **Enumeration pass** (single thread, orchestrator) — walks every source path, applies all
   exclusion + mode logic (Skip/Replace/KeepBoth), and builds two flat lists:
   - `IReadOnlyList<DirectoryWorkItem>` — source dir, dest dir, hidden flag
   - `IReadOnlyList<FileWorkItem>` — source path, planned dest path (already KeepBoth-renamed if
     applicable), action enum (Copy / SkipIdentical / Replace)
2. **Pre-create directories** — sorted by depth, sequential. Cheap, avoids worker contention.
3. **Parallel copy pass** — `Parallel.ForEachAsync(fileList, opts, ...)` with bounded
   concurrency. Each worker copies one file using the existing `CopyAndVerify` / `ExecuteCopy` /
   `ThrottledCopy` machinery (already pure per-file, just needs thread-safe counters).
4. **Mirror cleanup** — stays sequential. Destruction logic is small and deserves careful
   auditing more than parallelism.

### Concurrency tuning
New helper, e.g. `Services/DriveTypeService.cs`, uses `IOCTL_STORAGE_QUERY_PROPERTY` with
`StorageDeviceSeekPenaltyProperty` to detect SSD vs HDD per drive, plus `DriveInfo.DriveType`
for Network/Removable. Worker count by source+dest pair:

| Source | Dest    | Workers                                         |
|--------|---------|-------------------------------------------------|
| SSD    | SSD     | `Math.Min(8, Environment.ProcessorCount)`       |
| SSD    | HDD     | 1 (HDD seeks dominate)                          |
| HDD    | SSD     | 1 (HDD seeks dominate)                          |
| HDD    | HDD     | 1                                               |
| Net    | any     | 4 (good middle ground for SMB)                  |
| any    | Net     | 4                                               |
| Unknown| any     | 2 (conservative)                                |

If multiple sources span different drive types, take the most-constraining of all source/dest
combinations.

### Thread-safety touchpoints
- `TransferResult` counters → switch to `Interlocked.Increment` / `Interlocked.Add`. Properties
  exposed read-only; mutator methods on the result class (`AddCopied()`, `AddBytes(long)`, etc.).
- `TransferResult.AddFileError` → bounded under a `lock` (or `ConcurrentQueue` with a counter).
- `VssSnapshotService.GetOrCreateSnapshotRoot` → must be thread-safe. Internal cache should use
  `ConcurrentDictionary` keyed by volume root, value = `Lazy<SnapshotRoot>` so the snapshot
  creation itself runs once even under contention.
- `IProgress<int>` percent reporting — already free-threaded by contract. Calls from workers OK.

### Stages (commit-able boundaries)
1. **Stage 1: TransferResult thread safety** — switch counters to `Interlocked`, lock around
   `AddFileError`. Behavior identical single-threaded. SAFE TO COMMIT ALONE.
2. **Stage 2: DriveTypeService** — new helper + unit-of-test. No callers yet. SAFE TO COMMIT.
3. **Stage 3: Enumerate-then-copy refactor (still sequential)** — extract `EnumerateWorkItems`,
   build `DirectoryWorkItem`/`FileWorkItem` records, replace recursive `CopyItem` orchestration
   with enumerate→pre-create→sequential-foreach. Should produce identical outputs to today's
   code. THE BIG ONE. Regression-test before commit.
4. **Stage 4: Add parallelism** — swap sequential `foreach` for `Parallel.ForEachAsync` with
   `MaxDegreeOfParallelism` from DriveTypeService. Make `VssSnapshotService` thread-safe.
5. **Stage 5: Regression sweep** — Mirror, VSS fallback, ACL strip, hidden attrs, versioning,
   compression, throttling, checksum verify, cancellation mid-run, pause/resume.

### Out-of-scope for Phase 3
- `CompressAsync` (zip archive creation) — `ZipArchive` is not thread-safe for concurrent writes;
  parallelizing would require a different archive library. Skip.
- `ExtractAsync` — same reason.
- `MoveAsync` — uses `CopyItem` then `DeleteItem`; will inherit parallel speedup once `CopyItem`
  is refactored. No additional work needed.
