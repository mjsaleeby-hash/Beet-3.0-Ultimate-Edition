# Wave 2.5 — Pie polish, legend accessibility, percentage contrast

**Date:** 2026-08-05
**Status:** design approved, not yet implemented
**Plan item:** `improvements/IMPLEMENTATION-PLAN.txt` §5 item 2.5, checklist §9
**Source report:** `improvements/uxui-assessment.txt` (PIE-2, PIE-3, PIE-4, PIE-5, A11Y-2, A11Y-4)

## Why this wave exists

The pie chart renders the top-10 largest items plus an `"Other"` slice — up to **11 slices**,
with slice identity carried by **colour alone**. Standard part-to-whole guidance caps a pie at
roughly 6 categories and requires a percentage data table as the non-colour fallback.

The legend already *is* that table: swatch, icon, name, size, percentage. That single fact drives
the whole design. The legend is not decoration around the chart — it is the accessible
representation of the chart. So the work is to make the legend excellent and point assistive
technology at it, rather than to describe a canvas twice.

## Scope corrections found while designing

Three claims in the source documents did not survive checking. They are recorded here because
the plan's standing rule is to verify a report before implementing off it.

**1. Wave 1.5 was ticked without covering this control.** The Narrator pass landed 12
`AutomationProperties` and 2 `FocusVisualStyle` in `MainWindow.xaml` and nothing anywhere else.
`Views/PieChartControl.xaml` has neither, plus no `Focusable`, `IsTabStop` or `KeyDown`, while its
legend rows are `Cursor="Hand"` Borders wired to `MouseEnter`/`MouseLeave`/`MouseLeftButtonUp`.
They advertise interactivity while being mouse-only. Checklist entry downgraded to `[~]`
(commit `9120d9e`); remediation is folded into this wave. Note that `uxui-assessment.txt` had
already flagged this as PIE-5 `[HIGH-a11y]` — the gap was documented and simply never folded into
1.5's tick.

**2. `uxui-assessment.txt:100` is wrong about tiny slices.** It proposes suppressing
sub-threshold legend rows "(they're already rolled into `Other`)". They are not.
`BuildPieSlices` (`MainViewModel.cs:1951-2024`) takes the **top 10 by size**, and `Other` is only
`totalSize - top10Size` — items ranked 11 and beyond. A folder holding one 8 GB file and three
12-byte files puts all four in the top 10, so those tiny rows are *not* represented in `Other`.
Suppressing them would silently drop data and leave the legend not summing to 100%. This is the
same failure mode as 2.1 (`#hex` grep missed literal `White`), 2.3 (read a setter, never grepped
for a writer) and 1.5 above: reasoning from one code path without checking the one next to it.

**3. The donut-centre fix is light-only, not both themes.** §5 says "donut-center brush =
SurfaceBrush", but `DonutCenterBrush` was deliberately introduced in an earlier light-mode polish
round, and the two themes disagree for different reasons:

| Theme | `DonutCenterColor` | `SurfaceColor` | Reads as |
|-------|--------------------|----------------|----------|
| Light | `#F0F0ED`          | `#FFFFFF`      | grey-beige smudge on a white card — the reported bug |
| Dark  | `#121735`          | `#1A2045`      | inset well (exactly `PanelColor`) — deliberate |

Only light changes. Dark stays byte-identical, so there is no R4 argument to have.

## Evidence: the contrast claim, measured

A11Y-4 says "several palette colours fail WCAG 4.5:1". Measured against each theme's
`SurfaceColor`, **all eleven fail in light and all eleven pass in dark**:

| Colour | vs `#FFFFFF` (light) | vs `#1A2045` (dark) |
|--------|----------------------|---------------------|
| blue `#38A8EB` | 2.64 ❌ | 5.96 ✅ |
| rose `#F05C7A` | 3.22 ❌ | 4.88 ✅ |
| green `#2EBD60` | 2.45 ❌ | 6.41 ✅ |
| amber `#E89008` | 2.49 ❌ | 6.30 ✅ |
| violet `#9378E8` | 3.45 ❌ | 4.56 ✅ |
| red `#E85C5C` | 3.43 ❌ | 4.58 ✅ |
| sky `#289EDD` | 2.99 ❌ | 5.26 ✅ |
| orange `#EB822C` | 2.71 ❌ | 5.81 ✅ |
| emerald `#22B580` | 2.63 ❌ | 5.98 ✅ |
| fuchsia `#D466E8` | 3.06 ❌ | 5.14 ✅ |
| `Other` grey `#888898` | 3.49 ❌ | 4.51 ✅ |

An earlier round darkened four of these "for light mode contrast" and never got any of them over
the line — darkening a mid-tone saturated hue barely moves its luminance against white. Today the
*only* legend percentages readable in light are the negligible ones, because `IsNegligible` swaps
them to `SecondaryTextBrush`.

**The 12×12 swatches also sit below the 3:1 non-text threshold**, and are deliberately left alone:
WCAG 1.4.11 applies to graphics *required* to understand content, and name, size and percentage
are all present as text. The swatch is redundant colour-coding, not the sole carrier of meaning.

## Design

### 1. One visibility threshold

`PieSlice` gains `public const double MinVisiblePercentage = 0.05;` and
`IsNegligible => Percentage < MinVisiblePercentage` — the value it already has, now named. The
render guard stops testing sweep angle and uses the same constant.

The decision also **moves**. Today `CreateSlicePath` returns a `Data`-less `Path` on
`SweepAngle < 0.1`, and the caller still adds it to `_slicePaths`, still adds it to the canvas,
and still wires three mouse handlers to it — so hovering a tiny slice's legend row highlights an
invisible nothing. The render loop (`PieChartControl.xaml.cs:171-180`) will skip negligible slices
outright, so no phantom path is created. `HighlightSlice` already tolerates a missing path via
`FirstOrDefault` plus a null-conditional (`:300-301`); no change needed there.

**Intended behaviour change:** the two thresholds unify *upward*. `SweepAngle < 0.1` is 0.028%,
`IsNegligible` is 0.05%. Slices between those values currently draw a hairline wedge and will stop
drawing. 0.05 is chosen because it matches the existing `IsNegligible` semantics and doc-comment
rather than inventing a third number.

### 2. Donut centre, light only

`Themes/Light.xaml:94` — `DonutCenterColor` `#F0F0ED` → `#FFFFFF`. Dark untouched. The token
survives; two themes genuinely differing is what a token is for.

### 3. Center label clamp

`TotalSizeText` (`PieChartControl.xaml:29-32`) gains `MaxWidth="88"` and
`TextTrimming="CharacterEllipsis"`. The hole is 100×100 (`.xaml.cs:184-186`), leaving 6px per side.

### 4. Legend rows become focusable, keyboard-activatable Buttons

A `GhostRowButton` style in `PieChartControl.xaml`'s own `UserControl.Resources` — single
consumer, no reason to touch a theme dictionary. Its `ControlTemplate` root is the existing
`Border`, so `CornerRadius`, `Padding`, `Margin` and the `IsHighlighted`/`IsNegligible`
DataTriggers all carry over and the row looks unchanged.

- `MouseEnter`/`MouseLeave` stay — they drive `HighlightSlice`, not just visuals.
- `GotFocus`/`LostFocus` call the same `HighlightSlice`, so keyboard focus lights the matching
  wedge exactly as hover does. This is what makes the feature usable rather than merely compliant.
- `Legend_Click` changes signature from `MouseButtonEventArgs` to `RoutedEventArgs`.
  `Slice_Click` on the `Path` keeps its mouse signature.
- `PieSlice` gains a computed `Announcement` (`"Documents, 4.2 GB, 31.7 percent"`) bound to
  `AutomationProperties.Name`. Derived from `init`-only properties, so no `INotifyPropertyChanged`.
- A visible focus ring is required — the row must show focus, not just accept it.

Button was chosen over a `ListBox` conversion and over `Focusable` on the bare `Border`. Button
exposes the **Invoke** pattern, which matches what a row does (navigate into that folder), and
preserves single-click behaviour byte-for-byte per R6. `ListBox` would give arrow-key navigation
but introduces persistent selection semantics the control has never had, nests a `ScrollViewer`
inside the existing one, and pushes navigation onto Enter/double-click — a behaviour change in a
wave scoped to polish. A bare `Border` has no useful `AutomationPeer`, so it would need a
hand-written peer to announce properly: more work than the Button for a worse result.

### 5. Percentage text uses `PrimaryTextBrush`

`PieChartControl.xaml:97-99` drops the `FillColor` binding. The `IsNegligible` DataTrigger keeps
overriding to `SecondaryTextBrush`.

**This changes dark too**, where percentages currently pass contrast. In dark it is a consistency
change, not a fix. Plan §8 explicitly classes 2.5 as an intentional UX change, so this is in
bounds — but it must be *seen* before it ships, hence the mockup below.

### 6. Canvas marked so Narrator skips it

The canvas container gets an `AutomationProperties.Name` plus `HelpText` pointing at the legend.

**Open item, to be verified rather than assumed.** WPF has no `AccessibilityView="Raw"` (that is
WinUI). Whether the child `Path` elements leak into the automation tree as generic peers depends
on WPF's default peer behaviour, and this design does not claim to know. Verify with Accessibility
Insights during the manual pass; only if the paths do leak, add a custom `AutomationPeer`. Do not
mark this item done on the basis of the `Name` alone.

## Testing

Plan §6 is right that the light theme has no automated pixel test, but more is testable than that
implies:

Plan §8 R3 ("test-first for anything with logic") applies: the threshold unification and the
announcement string are logic, so their tests are written before the implementation.

- `IsNegligible` boundaries at 0.049 / 0.05 / 0.051
- `Announcement` formatting for a normal slice, for `"Other"`, and for a negligible slice
- `PieSlice.IsRenderable => !IsNegligible`, which is what the render loop calls. Naming it — rather
  than letting the control re-derive its own test — is the point: a test asserts the render
  decision and the legend's negligible styling read the *same* `MinVisiblePercentage`, so the two
  thresholds cannot drift apart a second time. The predicate is deliberately trivial; its value is
  that it gives the invariant a single home and a test.

Suite is currently 217 green; expect roughly 223. `MainViewModelTests` construct `MainViewModel`
and call `BuildPieSlices` / read `TopPieSlices`, so adding properties to `PieSlice` is safe.

Manual pass (R7): launch, open the pie chart in both themes, Tab through the legend and confirm
focus is visible and moves the wedge highlight, activate a row with Enter and with Space and
confirm it navigates identically to a click, and run Narrator over the panel.

## Sign-off mockup

`mockups/wave2-5-pie-legend.html`, following `mockups/wave2-1-theme-tokens.html`: dark and light
side by side, current vs proposed percentage colour, the light donut-centre change, a focused
legend row, a negligible row, and a value table. Reviewed **before** the XAML is written — it is
cheaper to reject a legend treatment in HTML than in XAML, and §6 otherwise defers all visual
judgement until after implementation.

## Files touched

- `Models/PieSlice.cs` — constant, `IsNegligible`, `Announcement`, renderability predicate
- `Views/PieChartControl.xaml` — ghost button style, percentage brush, label clamp, canvas naming
- `Views/PieChartControl.xaml.cs` — skip negligible in the render loop, `Click` signature, focus handlers
- `Themes/Light.xaml` — one colour value
- `BeetsBackup.Tests/` — new tests
- `mockups/wave2-5-pie-legend.html` — sign-off sheet

## Residual risks

- Tab order grows by up to 11 stops in the pie panel.
- Wedges between 0.028% and 0.05% stop rendering.
- Dark-mode percentages change colour without having been broken.
- The automation-tree question in item 6 is genuinely open until measured.

## Explicitly out of scope

Cutting the slice count to ~6, or adding a stacked-bar or treemap alternative view. That is a
redesign, not behaviour-neutral polish, and it is parked in `notes/ideas.md` for 4.0+. This wave
makes the existing chart reachable and readable; changing what the chart *is* needs its own
decision.
