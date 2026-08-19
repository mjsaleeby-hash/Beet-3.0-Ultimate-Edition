# Changelog — where the record lives

The dated files in this folder cover **2026-03-23 through 2026-04-14** and are the last hand-written changelog entries.

Development after 2026-04-14 was not recorded here. Roughly 100+ commits landed between then and 2026-08-18 — the dashboard redesign, mandatory elevation, the Launch-at-Startup move from a Startup-folder shortcut to an ONLOGON scheduled task, the telemetry channel and PerformanceMonitor cohort work, the 3.0 → 4.0 performance verdict, the launcher stub, and Waves 1 through 2.6.

**Do not treat the gap as "nothing changed."** For anything after 2026-04-14, use these instead:

| What you want | Where to look |
|---|---|
| What the app does today | [`../../README.md`](../../README.md) — kept current |
| End-user behavior | [`../user-guide.txt`](../user-guide.txt) |
| Support-facing detail and known quirks | [`../support-reference.md`](../support-reference.md) |
| Why a change was made | [`../superpowers/specs/`](../superpowers/) — design docs, one per wave |
| How a change was broken into tasks | [`../superpowers/plans/`](../superpowers/) |
| Audits, verdicts, and open specs | [`../../improvements/`](../../improvements/) |
| Decisions, bugs, and ideas | [`../../notes/`](../../notes/) |
| The literal change history | `git log` — commit messages in this repo are written to be read |

If hand-written changelog entries resume, add them here as `YYYY-MM-DD.md` following the format of the existing files, and delete the paragraph above once the gap is filled.
