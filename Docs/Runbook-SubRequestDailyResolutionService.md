# Runbook: Sub Request Daily Resolution Service

Hosted background service that resolves the day-of state of every sub
request whose absence date is today.

- **Code:** `src\NewHeights.TimeClock.Web\Services\SubRequestDailyResolutionService.cs`
- **Config class:** `src\NewHeights.TimeClock.Web\Services\SubRequestDailyResolutionOptions.cs`
- **Wired in:** `Program.cs` via `AddHostedService<SubRequestDailyResolutionService>()` + `Configure<SubRequestDailyResolutionOptions>(...)`
- **Shipped:** Phase C, 2026-04-27

## What it does

Once per day at `RunHour` (default 6 AM local), it walks every
`TC_SubRequests` row where `StartDate = today` and the status is not
already `AbsenceApproved`, `Cancelled`, or `Denied`. For each row it
applies one of three actions based on the current status:

| Source status | Action | Notify |
|---|---|---|
| `SubConfirmed` | Set status → `AbsenceApproved`, stamp `SupervisorApprovedBy = "system@auto-approve"` | Teacher + supervisor (email + SMS) |
| `PartiallyAssigned` | No status change | Supervisor only — email contains a Take-Over deep link |
| `AwaitingSub` / `Submitted` / `SubAssigned` | Set status → `Cancelled`. Also flips any `TC_SubOutreach` rows in `AWAITING` to `CANCELLED_BY_AUTO` so late-arriving sub responses can't poach a slot the system gave up on | Teacher + supervisor (email + SMS) |

### Grace window for emergencies

A request flagged `IsEmergency = 1` is **skipped** if it was created
less than `EmergencyGraceHours` (default 24) before the sweep ran. This
prevents the system from auto-cancelling a same-morning emergency
request before its cascade has had time to land coverage.

### SMS gating

For each notified recipient, SMS is dispatched only when **all** of:
`ISmsService.IsEnabled`, `recipient.Phone is not null`, and
`recipient.SmsOptedOut == false`. Email always fires when
`recipient.Email is not null`. Failures on either channel log a warning
but do not throw — the sweep continues to the next request.

## Configuration

Bound from `appsettings.json` section `"SubRequestDailyResolution"`.

```json
"SubRequestDailyResolution": {
  "Enabled": true,
  "RunHour": 6,
  "ScanIntervalMinutes": 15,
  "InitialDelayMinutes": 10,
  "EmergencyGraceHours": 24,
  "PortalBaseUrl": "https://clock.newheightsed.com"
}
```

| Key | Default | Notes |
|---|---|---|
| `Enabled` | `true` | Master switch. False keeps the service running but the sweep is a no-op |
| `RunHour` | `6` | Hour 0–23 local. Wall-clock gate is `now.Hour >= RunHour && now.Hour < RunHour + 2`, so a restart up to 2 hours after `RunHour` still picks the day up |
| `ScanIntervalMinutes` | `15` | How often the service wakes up. Dedup is via in-memory `LastRunDate` plus idempotent status transitions, so duplicate ticks are safe |
| `InitialDelayMinutes` | `10` | Delay after app startup before the first tick. Avoids firing during a 6 AM deploy |
| `EmergencyGraceHours` | `24` | Skip auto-cancel on emergency requests this fresh |
| `PortalBaseUrl` | `https://clock.newheightsed.com` | Origin for the Take-Over deep link in partial-coverage emails. Trailing slash trimmed |

## Audit codes

Every action writes a row to `TC_AuditLog` via `IAuditService` with
`Source = System`. Grep these codes when investigating:

| Action code | When written |
|---|---|
| `SUB_AUTO_APPROVED` | `SubConfirmed` → `AbsenceApproved` transition |
| `SUB_PARTIAL_DAY_OF` | Supervisor was notified about a partially covered request |
| `SUB_AUTO_CANCELLED` | `AwaitingSub` / `Submitted` / `SubAssigned` → `Cancelled` transition |

Also worth knowing (these are emitted by other paths but show up alongside in the audit timeline):

| Action code | Source |
|---|---|
| `SUB_TAKEN_OVER` | Supervisor clicked Take-Over on the Sub Calendar — `SubOutreachService.TakeOverRequestAsync` |
| `SUB_MANUAL_ASSIGN` | Supervisor hand-picked a sub via the Take-Over modal — `SubOutreachService.ManualAssignSubAsync` |

Constants: `NewHeights.TimeClock.Shared.Audit.AuditActions.SubOutreach`.

## How to disable temporarily

Three options, picked by how long you need it down:

**One sweep only (today's 6 AM tick already happened — nothing to do).**
The dedup guard prevents a second run today. No action needed.

**Skip a single morning before the tick.** Set
`SubRequestDailyResolution:Enabled` to `false` in Azure App Service
Configuration → Application settings → save → restart. Flip back to
`true` before tomorrow's tick. The service stays running; the sweep is
a no-op while disabled.

**Indefinite.** Same as above plus leave it disabled. The hosted service
itself stays registered (no code change required); turning `Enabled`
back to true resumes the next morning's sweep.

⚠ Don't comment out `AddHostedService<SubRequestDailyResolutionService>()`
in `Program.cs` to disable — that requires a redeploy and undoing it
later is more error-prone than flipping a config flag.

## How to verify it ran

Each sweep emits a structured log line with totals. In Kudu Live Log
Stream or Application Insights search for:

```
SubRequestDailyResolution sweep complete
```

Sample output:

```
SubRequestDailyResolution sweep complete: autoApproved=2, autoCancelled=1, partialNotified=0, skipped=0.
```

Per-request actions log at `Information` level when an emergency is
skipped:

```
Skipping recent emergency request 142 (age 4.2h < grace 24h)
```

Per-request errors log at `Error` level and include the request ID +
status. The sweep continues past errors — one bad request doesn't kill
the whole pass.

For SQL-side verification:

```sql
SELECT TOP 50 ActionCode, EntityId, DeltaSummary, CreatedDate
FROM TcAuditLog
WHERE ActionCode IN ('SUB_AUTO_APPROVED', 'SUB_AUTO_CANCELLED', 'SUB_PARTIAL_DAY_OF')
  AND CreatedDate >= CAST(GETDATE() AS DATE)
ORDER BY CreatedDate DESC;
```

## When something's wrong

| Symptom | Likely cause |
|---|---|
| Sweep didn't run at 6 AM | App Service was cold-starting through the wall-clock window. The wall-clock gate is `RunHour..RunHour+2`, so a 6 AM cold start that finishes at 8:05 AM has missed today. Manual: bump `RunHour` lower next morning, or flip `Enabled=false` to skip a stale day |
| Teacher reports request was cancelled overnight but they thought it was emergency | Check `IsEmergency` and `CreatedDate` on the row. If `CreatedDate` is older than `EmergencyGraceHours`, it was outside the grace window and auto-cancel was correct |
| Supervisor didn't get the partial-coverage email | `ResolveSupervisorEmployeeAsync` first tries `SupervisorApprovedBy` (email match) then falls back to the teacher's `SupervisorEmployeeId`. If both are null, log will show `"PartiallyAssigned request {Id} has no resolvable supervisor — skipping"` |
| SMS didn't go out | Check the recipient's `SmsOptedOut` flag, `Phone` value, and whether `ISmsService.IsEnabled` is true in the active environment. SMS failures log warnings but don't throw |
| Late sub acceptance turns up after auto-cancel | Expected. `AutoCancelAsync` flips outstanding `TC_SubOutreach` rows to `CANCELLED_BY_AUTO`, so the cascade can't re-attach. If a sub did accept before the sweep tick, they'd still see "request unavailable" on the respond page — that's the gap-1 server guard from migration 053-era work |

## Idempotency notes for code reviewers

- `LastRunDate` is a `DateOnly` field on the singleton hosted service. It
  resets on app restart but only causes one extra tick (the wall-clock
  gate plus `LastRunDate >= today` short-circuit catches it).
- Status transitions only flip from the **expected** source state, so a
  duplicate tick after a partial save can't re-cancel an already-cancelled
  request.
- The audit log will show duplicate `SUB_AUTO_*` rows if a tick truly
  duplicates. Treat double rows as a deploy / restart artifact rather
  than a logic bug.

---

End of runbook.
