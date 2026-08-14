# Wave 2.6 — De-dup Helpers — Design

**Status:** approved 2026-08-14
**Supersedes:** the single `[ ] Wave 2.6` line in `improvements/IMPLEMENTATION-PLAN.txt` §9 and the
one-line scope in §5 item 2.6.

## Why this document exists

Wave 2.6 was specified in `improvements/IMPLEMENTATION-PLAN.txt` §5 as four de-dup items lifted
from the architecture report (D4, D6, D7, D8), later joined by two inherited cleanups. Before
planning it, every one of those six claims was checked against the code, because the
`improvements/` reports have now been wrong on premise three times (§FIX F's dead property, the
FAT re-copy non-repro, and `FolderSizeCompleted` being blind to HDDs).

The audit found four claims sound, one wrong in a way that would have caused a real defect, and
one real but mis-shaped. It also found two incorrect code comments and one latent bug. This
design is scoped to what survived.

### Audit results

| Item | Claim | Verdict |
|---|---|---|
| D6 | `IOExceptionClassifier` — raw HResult checks repeated | **SOUND.** 9 sites in `TransferService.cs`, no other file masks an HResult |
| D4 | `ProgressPercent(result)` — duplicated computation | **SOUND with a caveat.** Same 5-term sum at `:731` and `:819`, but the call sites differ in what they do with it |
| D7 | "collapse the 4 uniqueness helpers to 2" | **WRONG AS WRITTEN.** They are not 4 of a kind; 4 → 2 changes behaviour. Correct target is 4 → 3 |
| D8 | `BackupLogEntryFactory` | **REAL, WRONG SHAPE.** 4 sites, all in one file, in two pairs taking different input types. One factory does not fit |
| — | `SearchFilter` is dead | **CONFIRMED.** Only the definition (`MainViewModel.cs:90`) and its own doc-comment (`:598`). No writer |
| — | `AccentFocusVisual` is duplicated by Wave 2.5 | **OVERSTATED.** Two styles with deliberately different geometry, not duplicates |

**D4 is deferred, not dropped.** It is sound, but it is a third `TransferService` edit and the wave
already carries two. It is recorded in §9 as inherited by a later cleanup pass rather than
implemented here, so 2.6b stays two commits and its gate stays legible.

## Structure

Two sub-waves with a hard gate between them. `IMPLEMENTATION-PLAN.txt` §9's single `Wave 2.6`
line becomes `Wave 2.6a` and `Wave 2.6b`, so the checklist records an outcome per shippable unit.

**Nothing in 2.6a depends on 2.6b, and nothing in 2.6b depends on 2.6a.** 2.6a is deployable on
its own even if 2.6b is dropped entirely.

### Wave 2.6a — inert cleanup (5 commits)

None of these can alter a byte of transfer behaviour.

| # | Commit | Files |
|---|---|---|
| 1 | Retire the dead `SearchFilter` property and its always-true predicate | `ViewModels/MainViewModel.cs` |
| 2 | Move both focus-visual styles into `Themes/Controls.xaml` | `Themes/Controls.xaml`, `Views/MainWindow.xaml`, `Views/PieChartControl.xaml` |
| 3 | Collapse the four `BackupLogEntry` constructions into three private statics | `Services/SchedulerService.cs` |
| 4 | Correct two doc comments that describe behaviour the code does not have | `Services/NameSanitizer.cs`, `Services/TransferService.cs` |
| 5 | Record 2.6a, log the archive-naming defect, open the 2.6b gate | `improvements/IMPLEMENTATION-PLAN.txt`, `notes/decisions.md`, `notes/bugs.md` |

### Wave 2.6b — engine work, gated (2 commits)

| # | Commit | Gate |
|---|---|---|
| 1 | Extract `IOExceptionClassifier` | Classifier unit tests + a site table the diff is checked against |
| 2 | Collapse the uniqueness helpers 4 → 3 | Characterization tests for the three unpinned behaviours, written **first** |

## 2.6a details

### 1. Retire `SearchFilter`

`MainViewModel.SearchFilter` has no writer in the product. The pane search boxes bind
`DeepSearchQuery` (`MainWindow.xaml:1345`, `SplitPaneWindow.xaml:312`), a different mechanism.
The predicate in `CreateFilteredView` short-circuits on `string.IsNullOrEmpty(_searchFilter)` and
therefore returned `true` on every call ever made.

Remove the backing field, the property (including the two `Refresh()` calls in its setter), and
the `view.Filter` assignment — the predicate body reads `_searchFilter`, so it cannot outlive the
field. `CreateFilteredView` survives as a one-line pass-through:

```csharp
private ICollectionView CreateFilteredView(ObservableCollection<FileSystemItem> source)
    => CollectionViewSource.GetDefaultView(source);
```

**Deliberately not done:** deleting `CreateFilteredView` and the `_filteredTopView` /
`_filteredBottomView` fields to bind panes straight to the collections. WPF resolves an
`ItemsSource` binding to the collection's default view anyway, so the end state is equivalent —
but it touches pane `ItemsSource` bindings, which is where Wave 2.2's Reset tuning lives. Not
this wave's risk to take.

Behaviour-neutral: an always-true filter and no filter produce identical views.

### 2. Move both focus-visual styles to `Themes/Controls.xaml`

`AccentFocusVisual` (`MainWindow.xaml:188`, consumed once at `:205` by the `ToolbarButton` base
style) is stranded in a `Window.Resources` and unreachable from a `UserControl`. Wave 2.5 hit
this and wrote a local `LegendRowFocusVisual` in `PieChartControl.xaml`, describing it in a
comment as a deliberate duplicate.

**That comment overstates it.** The two share a `ControlTemplate` structure — `Rectangle`,
`AccentBrush` stroke, `StrokeThickness 2`, `SnapsToDevicePixels` — but differ in geometry, each
tuned to the corner radius it wraps:

| Style | Margin | Radius | Wraps |
|---|---|---|---|
| `AccentFocusVisual` | -2 | 9 | toolbar buttons, `CornerRadius 8` |
| `LegendRowFocusVisual` | -1 | 5 | legend rows, `CornerRadius 4` |

WPF offers no clean way to parameterize a `FocusVisualStyle` against the focused element's corner
radius, so one shared style would mean one geometry for both — a visible change to a legend ring
signed off nine days ago.

**Both styles move to `Themes/Controls.xaml` as two named styles, geometries verbatim.**
`Controls.xaml` is merged in `App.xaml:11`, so both become reachable app-wide. `MainWindow.xaml`
keeps `{StaticResource AccentFocusVisual}` unchanged; `PieChartControl.xaml` deletes its local
copy and references the moved one. **Zero visual change in either theme.**

Wave 2.5's comment is corrected to say the geometries are intentionally different rather than
implying an unresolved duplication.

### 3. Three private statics in `SchedulerService`

Four `new BackupLogEntry` sites, all in `SchedulerService.cs`, in two pairs with different inputs:

| Sites | Shape | Built from |
|---|---|---|
| `:305`, `:329` | Placeholder — `JobId`, `JobName`, `SourcePath`, `DestinationPath`, `Status`, `Timestamp`, `Message` | `ScheduledJob` |
| `:505` | Full `Running` — adds `SourcePaths`, `StripPermissions`, `TransferMode` | `ScheduledJob` snapshot |
| `:742` | Full `Running` — same fields, name suffixed `" (retry)"` | a failed `BackupLogEntry` |

```csharp
private static BackupLogEntry PlaceholderFor(ScheduledJob job, BackupStatus status, string message)
private static BackupLogEntry RunningFor(ScheduledJob job)
private static BackupLogEntry RetryOf(BackupLogEntry failed)
```

Private statics, not a public factory type: every caller is in this one file. A
`Services/BackupLogEntryFactory.cs` would create a public type with a single consumer, and putting
them on the model would make `Models` depend on `ScheduledJob`, which it currently does not.
Promoting them later is trivial if a second consumer appears.

Behaviour-neutral: identical field values, same order of `_log.Add` calls.

### 4. Correct two wrong comments

Both describe behaviour the code does not have:

- **`Services/NameSanitizer.cs:27`** — claims archive collisions "produce `" (2)"` suffixes via
  `TransferService.GetUniqueFilePath`". `GetUniqueFilePath` produces `-1`, `-2`. The `" (2)"`
  format belongs to `ReserveUniquePrefix`, a different helper. Wrong function *and* wrong format.
- **`Services/TransferService.cs:245-246`** — says "Disambiguate **if** a prior archive with this
  exact name already exists" and documents `"name.zip" → "name (2).zip"`. The call is
  unconditional and the format is `-N`. See the defect below.

### 5. Records, including the archive-naming defect

**`GetUniqueFilePath` can never return the original path.** Its `do/while` builds
`{name}-{counter}{ext}` starting at `counter = 1` *before* testing existence, so it always returns
a suffixed name.

Its two callers differ:

- `:704` (KeepBoth file case) is guarded by `if (File.Exists(destFile))` at `:673`. **Correct.**
- `:247` (archive naming) is **unguarded**. Every compressed archive is therefore named
  `MyBackup_2026-08-14_10-30-00-1.zip` — the `-1` is always present, even on a clean destination.
  Because the timestamp has second resolution, real collisions are rare, so the suffix is
  essentially always spurious.

Severity: cosmetic. No data loss, no overwrite risk — a stray `-1` in a filename. It has been
shipping. There are **no tests over the compress path at all**; the only `Compress` hit in the
suite is an unrelated `DiskSpaceService.Preview` test.

**This is logged in `notes/bugs.md` and deliberately NOT fixed in Wave 2.6.** Changing archive
filenames is user-visible behaviour, and 2.6 is behaviour-neutral by contract. Fixing it is a
separate decision on its own line.

## 2.6b details

### 1. `IOExceptionClassifier`

New `Services/IOExceptionClassifier.cs`, a static class matching the existing convention
(`NameSanitizer`, `ExclusionMatcher`, `DriveTypeService`). No DI registration and no interface:
it is a pure predicate over an exception with nothing to substitute in a test.

```csharp
public static bool IsSharingViolation(IOException ex)
public static bool IsDiskFull(IOException ex)
```

Typed to `IOException` because all nine call sites already catch it; widening to `Exception`
would invite use where the HResult convention does not hold. The Win32 codes stay **private
consts** — `ERROR_SHARING_VIOLATION = 0x0020`, `ERROR_HANDLE_DISK_FULL = 0x0070`. Exposing them
would re-create the magic-number problem one layer up.

The file records why the mask exists: a Win32 error surfaces as an HRESULT of `0x8007xxxx`, so
`HResult & 0xFFFF` recovers the raw code. This is currently implicit at all nine sites and
explained at none.

**Filter-safety constraint.** These calls sit inside `catch (...) when (...)` exception filters.
A filter that throws is silently treated as `false` — which would turn a handled disk-full into an
unhandled crash. Both predicates are integer arithmetic on a property that cannot throw, so they
are provably safe. Recorded as a deliberate check and as a constraint on anyone extending the
class: **no I/O, no allocation, no logging inside a classifier.**

**Site table — the R6 evidence.** The diff is checked against this line by line.

| Line | Current | Becomes |
|---|---|---|
| 376 | `(ioEx.HResult & 0xFFFF) == 0x0020` | `IOExceptionClassifier.IsSharingViolation(ioEx)` |
| 493 | `(ioEx.HResult & 0xFFFF) == 0x0020` | `IOExceptionClassifier.IsSharingViolation(ioEx)` |
| 623 | `(ioEx.HResult & 0xFFFF) == 0x0020` | `IOExceptionClassifier.IsSharingViolation(ioEx)` |
| 653 | `(ioEx.HResult & 0xFFFF) == 0x0020` | `IOExceptionClassifier.IsSharingViolation(ioEx)` |
| 802 | `(ioEx.HResult & 0xFFFF) == 0x0020` | `IOExceptionClassifier.IsSharingViolation(ioEx)` |
| **863** | `(ioEx.HResult & 0xFFFF) == 0x0020 && !usedVss` | `IOExceptionClassifier.IsSharingViolation(ioEx) && !usedVss` |
| 616 | `(ioEx.HResult & 0xFFFF) == 0x0070` | `IOExceptionClassifier.IsDiskFull(ioEx)` |
| 646 | `(ioEx.HResult & 0xFFFF) == 0x0070` | `IOExceptionClassifier.IsDiskFull(ioEx)` |
| 795 | `(ioEx.HResult & 0xFFFF) == 0x0070` | `IOExceptionClassifier.IsDiskFull(ioEx)` |

`:863` is the single site carrying an extra condition, and the one where a careless
find-and-replace drops it. Dropping `!usedVss` would send the code down the VSS retry path a
second time.

**Tests.** On the classifier directly, constructing `IOException` with a set `HResult`:

- `0x80070020` → sharing violation, not disk full
- `0x80070070` → disk full, not sharing violation
- bare `0x20` → still classifies (the mask is the point)
- `0x80070021` → neither (guards an off-by-one in the constant)

The nine substitutions get no tests of their own. They are covered by the existing transfer suite
plus the site table; tests that merely re-assert the table would be the tautology trap caught in
Wave 2.4.

### 2. Uniqueness helpers, 4 → 3

The four are not four of a kind:

| Helper | Line | Contract | Fate |
|---|---|---|---|
| `GetUniqueFilePath` | 1212 | Counter loop, `-N`, `File.Exists`, never returns original | Merge |
| `GetUniqueFolderPath` | 1227 | Counter loop, `-N`, `Directory.Exists`, never returns original | Merge |
| `GetUniqueFolderPathReserved` | 1598 | **Returns the original when free AND unclaimed**; mutates a reservation set | **Stays separate** |
| `ReserveUniquePrefix` | 1390 | Names not paths, no filesystem, `" (2)"` format, `"root"` fallback | **Stays untouched** |

The first two differ only in their existence predicate and extension handling, and merge into one
private helper taking the predicate:

```csharp
// Splits on extension only when there is one, so folders (no extension) and files
// share one loop. The existence test is injected because that is the ONLY real
// difference between the two callers — File.Exists vs Directory.Exists.
private static string NextFreePath(string path, Func<string, bool> exists)
```

Exact parameter names and the call-site wiring are the implementation plan's business; what this
design fixes is that there is **one** loop, that it is injected with its existence test, and that
it preserves "never returns the original" for both current callers.
`GetUniqueFolderPathReserved` has a genuinely different
contract — folding it in would put a branch in the shared helper that only one caller ever takes.
`ReserveUniquePrefix` is a different family entirely; merging it would change archive naming,
which is user-visible.

**4 → 2 as originally specified would have changed where files land.** That is the finding that
justified this audit.

**Characterization tests, written before the collapse.** Three behaviours are currently unpinned:

- `GetUniqueFolderPath` — exact names on folder collision
- `GetUniqueFolderPathReserved` — two sources with the same folder name inside one run, against
  both the live filesystem and the reservation set
- `ReserveUniquePrefix` — the `" (2)"` sequence and the `"root"` empty-name fallback

Plus one test pinning **archive naming as it actually behaves** (`-1` on a clean destination). Its
comment states explicitly that it documents a known defect and names the `notes/bugs.md` entry, so
a future reader does not "fix" the test.

The file-path side needs nothing new: `CopyAsync_KeepBoth_MultipleExisting_IncrementsCorrectly`
(asserts exactly `file-2.txt`) and `KeepBoth_50Collisions_IncrementingNames` (asserts 50 files,
`report.txt` and `report-49.txt` present) already pin exact names through the public API. A change
to the suffix format or starting index fails them immediately.

### Off-ramp

If the `GetUniqueFolderPathReserved` characterization test proves awkward to write, **that
difficulty is evidence D7 is under-tested for its risk, and D7 is dropped** the way Wave 2.3 was —
with the reason recorded in §9. It does not proceed on the strength of "the refactor looks
obviously equivalent."

D6 and D7 are independent commits; dropping D7 does not affect D6.

## Constraints

From `improvements/IMPLEMENTATION-PLAN.txt` §8. Every task inherits these.

- **R1** — Heavy, deliberate commenting. Every non-obvious change carries a comment saying *why*.
- **R2** — One logical change per commit.
- **R3** — Test-first for anything with logic.
- **R4** — Dark-theme safety. **No theme dictionary is modified in this wave at all**; the focus
  styles move between files with their values verbatim.
- **R6** — Behaviour-preserving throughout. This wave has **no stated exceptions** — unlike 2.5,
  which had two. Anything that would change observable behaviour, including the archive-naming
  defect, is logged rather than fixed.
- **R7** — Manual smoke test recorded. **Pin the binary by `ProductVersion` before recording the
  pass** — a clean result against the deployed build proves nothing if the deployed build predates
  the change. This cost a discarded pass on 2026-08-13.
- Suite is **230 green** at the start of this wave. It must be green at the end of every commit.
- Deploy is `dotnet publish`, never `dotnet build` — `CopyExeToRoot` fires `AfterTargets="Publish"`.

## Success criteria

1. Suite green at every commit; total ≥ 230 and rising with the new tests.
2. Zero visual change — both themes, toolbar focus rings and legend focus rings identical to today.
3. `SearchFilter` gone; no reference remains outside the git history.
4. `IOExceptionClassifier` owns all nine HResult checks; no raw mask survives in `TransferService`.
5. Uniqueness helpers at 3, with `ReserveUniquePrefix` and `GetUniqueFolderPathReserved` intact and
   their distinct contracts documented — **or** D7 dropped with the reason recorded.
6. The archive-naming defect is in `notes/bugs.md`, unfixed and clearly scoped.
7. R7 pass recorded against a deployed build whose `ProductVersion` was checked first.
