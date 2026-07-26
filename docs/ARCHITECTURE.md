# LeanPlay architecture

## Technology choice

The privileged host is C# on .NET 8, not C++. The service is mostly lifecycle coordination, durable state management, Windows API calls, and data plumbing. Managed code reduces memory-safety risk and makes state-machine testing straightforward. Native Windows APIs remain available through narrow P/Invoke adapters. A custom driver is intentionally out of scope.

## Runtime boundaries

```text
LeanPlay.UI (future, standard user)
        |
        | authenticated named-pipe commands and read-only reports
        v
LeanPlay.Service (LocalSystem)
  +-- GameDetectionWorker
  |     +-- WMI Win32_ProcessStartTrace / Win32_ProcessStopTrace
  |     `-- low-frequency process reconciliation safety net
  +-- OptimizationCoordinator
  |     +-- policy validation
  |     +-- write-ahead recovery journal
  |     +-- Windows service controller
  |     `-- power-plan controller
  +-- Telemetry collectors (incremental)
  |     +-- ETW session
  |     +-- PDH counters
  |     `-- network probes
  `-- SQLite reporting store
```

The recovery journal is a small atomic JSON file, separate from SQLite. Recovery must still work when reporting storage is locked, unavailable, or damaged. SQLite is the queryable history, not the source of truth for rollback.

## Game-launch detection loop

`GameDetectionWorker` subscribes to `Win32_ProcessStartTrace` and `Win32_ProcessStopTrace`. Event handlers only copy process name, PID, and exit status into a channel; all decisions happen on one worker thread.

The order is important:

1. start both WMI watchers;
2. enumerate already-running target executables once to close the startup race;
3. deduplicate by PID;
4. on a target start, select the exact executable profile and call `BeginSessionAsync`;
5. on any matching PID death, call `EndSessionAsync` immediately, regardless of exit status;
6. reconcile target PIDs at a low frequency so watcher failure cannot leak optimized state;
7. if the host is stopping while a session is active, roll back in `StopAsync`;
8. on host startup, recover any durable non-idle journal before accepting new game events.

This uses passive WMI traces for the hot path. Reconciliation queries only process identity and runs every five seconds by default; it does not poll game memory or high-frequency performance counters.

## Snapshot and mutation state machine

```text
                 service startup with journal
                              |
                              v
Idle -> SnapshotCaptured -> Applying -> Active
          |                   |          |
          |                   |          | game exit, game crash,
          |                   |          | host stop, or reconciliation
          |                   v          v
          +--------------> RollbackPending
                                  |
                          +-------+--------+
                          |                |
                          v                v
                        Idle       RecoveryRequired
                                           |
                                           `-- retry on startup / --recover
```

There is no durable `Idle` file: absence of the journal means idle.

For every mutation, the coordinator:

1. captures the complete target state;
2. flushes the overall snapshot to disk;
3. appends a mutation record with `IntentRecorded` and flushes it;
4. invokes the Windows API;
5. marks the mutation `Applied` and flushes again.

If the process dies between steps 3 and 5, recovery conservatively restores an `IntentRecorded` mutation. Restore operations are idempotent and use the original snapshot, so this is safe whether the Windows call happened fully, partially, or not at all.

Rollback processes mutations in reverse order. Each successful restoration is durably marked. A failed restoration keeps the journal in `RecoveryRequired`; startup and the explicit recovery command retry it. The service never silently deletes an incomplete journal.

## Service changes

The MVP changes only runtime status via the Service Control Manager. It does not modify service startup type or registry configuration, so copying service registry keys would add risk without improving rollback. A service that was running is restarted; a service that was already stopped is left stopped.

Rules are fail-closed:

- explicit user approval is mandatory;
- protected/critical service names are denied;
- running dependent services cause rejection;
- access denied, invalid state, timeout, and missing service are captured per target;
- a required rule failure aborts activation and rolls back;
- an optional rule failure is reported and the session continues without that optimization.

## Power plan

The adapter uses documented `PowerGetActiveScheme` and `PowerSetActiveScheme` calls. The original GUID is captured and restored. HAGS is excluded because it is not a meaningful per-game, no-reboot toggle.

## Concurrency

One async lock serializes activation, rollback, and recovery. Duplicate WMI events are idempotent by PID. The MVP permits one active optimized profile at a time; a second distinct game is observed but not optimized until the first profile is restored.

## Security and anti-cheat boundary

- no game handles beyond normal process identity/existence checks;
- no memory reads, DLL injection, graphics hooks, input synthesis, or protected-process operations;
- no undocumented suspend APIs;
- no anti-cheat service rules;
- the future UI must use an ACL-restricted named pipe and schema-validated commands;
- every privileged action has a durable audit record.

## Telemetry architecture

Collection and attribution are separate:

- collectors emit timestamped facts;
- a session correlator aligns those facts with frame-time or ping spikes;
- attribution is probabilistic and includes confidence and competing causes;
- reports say “correlated with,” not “caused by,” unless an experiment isolates the variable.

PresentMon-compatible ETW events are the intended external frame-time source. DPC/ISR and NDIS collection should be added only after measuring collector overhead and confirming provider behavior alongside Vanguard.
