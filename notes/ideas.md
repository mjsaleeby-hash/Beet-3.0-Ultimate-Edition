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
- **Bandwidth / speed throttling** — cap the transfer rate to avoid saturating the disk or network during active use
- **Pre / post backup scripts** — run a user-specified command (batch file, PowerShell script) before and/or after a job completes
- **VSS / Volume Shadow Copy** — use Windows VSS to copy files that are currently open and locked by other processes (e.g. Outlook PST, database files)
- **Dry run / preview mode** — show exactly which files would be copied, skipped, or deleted by a job without actually making any changes; especially important for Mirror mode
- **Delta / block-level copying** — transfer only the changed byte ranges of large files rather than re-copying the entire file (useful for VM images, large databases)
- **Email / toast notifications** — alert the user on job completion or failure via Windows toast notification or email

**Explicitly out of scope:**
- Cloud integration (user does not want this)
