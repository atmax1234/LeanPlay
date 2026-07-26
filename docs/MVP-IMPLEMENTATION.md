# MVP implementation plan

## Delivered foundation

The first vertical slice covers the safety-critical path:

- CS2 and Valorant executable profiles;
- WMI start and stop events plus low-frequency reconciliation;
- crash-safe activation and rollback coordinator;
- service runtime-state and power-plan adapters;
- startup, shutdown, and explicit emergency recovery;
- SQLite schema and session/action persistence;
- deterministic core tests with fake Windows adapters.

## Next increments

### 1. Observation-only baseline

Add low-overhead collectors and benchmark their overhead before enabling any optimizations:

- PDH process CPU, working set, disk bytes, and system commit;
- ETW process start/stop, disk I/O, Defender, Windows Update, DPC/ISR, and Present events;
- IP Helper counters plus a configurable ICMP/UDP-safe endpoint for RTT, jitter, and loss;
- session report with clocks normalized to one monotonic timeline.

The acceptance gate is less than 0.5% average CPU on the target test machines, no measurable frame-time regression, and no Vanguard alerts.

### 2. Explainable correlation

Detect spikes from robust rolling baselines rather than fixed thresholds. Rank overlapping activity by temporal proximity and magnitude. Preserve raw samples so every explanation is reproducible.

Reports must distinguish:

- local CPU/disk scheduling interference;
- local network queue or competing transfer;
- first-hop/router loss;
- upstream routing or server latency;
- correlation with insufficient evidence.

### 3. Approved optimization experiments

Expose recommendations in the UI, never automatic guesses. Each recommendation needs:

- measured evidence from a baseline;
- expected mechanism;
- risk and reversibility statement;
- an A/B comparison with confidence intervals;
- one-click removal of the persistent rule.

### 4. Comparison mode

Pair comparable sessions by game build, map/workload, driver, and hardware. Compute average and percentile frame time, 1% low FPS from frame-time percentiles, RTT/jitter/loss, background CPU/disk/network, and bootstrap confidence intervals. Do not claim improvement from one uncontrolled run.

## Assumptions rejected

- **“High performance” is always faster:** modern CPUs often boost correctly on Balanced; this must be tested per machine.
- **CPU affinity reduces stutter:** it can fight the scheduler and hybrid-core policies. It remains observation/experiment-only.
- **DSCP lowers internet ping:** markings help only when each relevant network segment honors them. The MVP does not market this as ping reduction.
- **Stopping telemetry services improves games:** without measured overlapping work, an idle service is not interference.
- **HAGS is a runtime profile setting:** it is reboot-scoped and workload/driver dependent.
- **Terminate and restart equals pause:** process state and command lines cannot always be safely recreated. The MVP does not use undocumented process-suspend APIs.
- **A spike correlation proves causation:** reports attach confidence and use careful wording.

## Release gates

1. zero persistent settings after every test, including injected crashes at each journal write boundary;
2. successful restoration after access denied, service timeout, host kill, and machine restart;
3. no interaction with game or anti-cheat memory/process controls;
4. signed binaries and installer, restricted IPC ACL, and least-privilege review;
5. collector overhead and anti-cheat compatibility validated on dedicated CS2 and Valorant accounts/test machines;
6. baseline-versus-optimized evidence shows repeatable benefit before any rule ships as a recommendation.
