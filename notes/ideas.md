## 2026-03-29 — UI Overhaul Discussion

### UI Modernization Options Evaluated

Four approaches were explored for overhauling the application UI. A mockup was produced (`mockups/beets-backup-v2.html`) to prototype the recommended direction.

---

**Option A: Restyle existing WPF — Recommended**
- Current codebase has a solid foundation: DynamicResource theme system, custom ControlTemplates, virtualized ListViews
- A pure XAML/style pass can achieve ~95% of a modern look without touching ViewModel or service code
- Lowest effort, zero risk to existing tested business logic

**Option B: Full browser-based (Electron/Tauri) — Not recommended**
- Would require rewriting all C# services in JS/Rust
- Existing 93-test xUnit suite would be lost
- Drag-and-drop with Windows shell is painful in browser environments
- Adds startup latency and memory overhead

**Option C: WPF + WebView2 hybrid — Not recommended for core UI**
- Drag-and-drop does not compose across the WPF/WebView2 boundary
- Selection state would live in two separate worlds
- Could be viable for an isolated dashboard panel, but not the file browser panes

**Option D: WinUI 3 migration — Future consideration only**
- Would provide real Mica material, Fluent animations, native BreadcrumbBar
- Estimated 4–8 week XAML rewrite; not justified by the current visual gap

---

### Mockup: `mockups/beets-backup-v2.html`

Key design changes proposed in the mockup:

- Slim 76px header (down from ~200px) with compact command bar
- Pill-style toggles replacing checkboxes
- Breadcrumb path bars above each file pane
- Color-coded SRC / DST badges on panes
- Inline transfer progress banner between panes with Pause / Stop controls
- Modern 28px file list rows with sticky column headers
- Segoe Fluent Icons replacing emoji
- CornerRadius 6–8px throughout; subtle drop shadows for elevation
- Clean status bar at the bottom

**Status: Under consideration** — user is reviewing the mockup before committing to implementation.

---

### Bandwidth Throttling — Implemented Today

- Dashboard "Limit Speed" checkbox (fixed 10 MB/s cap)
- Scheduler speed picker with 1–100 MB/s options
- Chunked streaming with stopwatch-based rate limiting in `TransferService`
- Item previously tracked as a future feature in this file — now complete; removed from the pending list below

---

## 2026-03-28

### Future Feature Discussion — Backup Software Parity

Discussion of features common in other backup tools that Beets Backup does not yet have.

**Implemented this session (no longer pending):**
- Mirror (sync) transfer mode
- Exclusion filters (by extension pattern and exact name)
- Backup verification in scheduled jobs (SHA-256 via schedule dialog)
- Backup size estimation
- Pause / resume for scheduled backups

---

**Not yet implemented — candidates for future sessions:**

- **File versioning** — keep N previous versions of a changed file instead of overwriting; store older versions in a versioned subfolder or with a timestamp suffix
- **Compression** — zip or archive the backup output to save destination disk space
- **Encryption** — encrypt backups at rest so destination files are unreadable without a key
- **Pre / post backup scripts** — run a user-specified command (batch file, PowerShell script) before and/or after a job completes
- **VSS / Volume Shadow Copy** — use Windows VSS to copy files that are currently open and locked by other processes (e.g. Outlook PST, database files)
- **Dry run / preview mode** — show exactly which files would be copied, skipped, or deleted by a job without actually making any changes; especially important for Mirror mode
- **Delta / block-level copying** — transfer only the changed byte ranges of large files rather than re-copying the entire file (useful for VM images, large databases)
- **Email / toast notifications** — alert the user on job completion or failure via Windows toast notification or email

**Explicitly out of scope:**
- Cloud integration (user does not want this)
