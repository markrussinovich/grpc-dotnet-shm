# Grpc.Net.SharedMemory — environment-variable reference

This document enumerates the environment variables that influence the
shared-memory transport at runtime. **All variables are opt-in** and the
**production default behaviour does not require any of them to be set**.
The transport works out-of-the-box; these knobs exist for benchmarking,
diagnostics, and incremental rollout of new wake paths.

> ⚠️ These knobs are **read once at process load** (during static field
> initialization). Setting them after the library has touched
> `ShmConstants` / `Segment` etc. has no effect.

## Production wake / flow-control toggles

| Variable | Default | Purpose |
|---|---|---|
| `SHM_WIN_ALLOW_SPIN` | unset (=disable spin) | Legacy escape hatch on Windows. When set to `1`, restores the pre-2026 adaptive outer-spin behaviour in `ShmRing.WaitForData` / `WaitForSpace`. Costs ~5 % unary RT but recovers ~5.5× streaming-0B RT in pathological tight ping-pong cases. Production default is now no-spin (matches the Linux eventfd protocol). |
| `SHM_EVENTFD_WAKE` | unset | Linux only. When set to `1`, opts the data-segment wake path into the new eventfd / SCM_RIGHTS implementation (see `LinuxDataSegWaker`). Default keeps the futex path. Mismatched peers (one side `1`, other side futex) negotiate during the connect handshake via `Segment.FinalizeDataSegWaker`. |
| `SHM_SAW_WRITERLOOP` | unset | Windows only. When set to `1`, enables the server WriterLoop's `SignalObjectAndWait` fast path: the deferred `SignalData` and the Phase 3 wait are combined into a single Win32 syscall, saving ~5–10 µs per RT under no-spin operation. |

## Diagnostic counters (zero overhead when unset)

| Variable | Effect |
|---|---|
| `SHM_WIN_DIAG=1` | Enables `WindowsRingSync.GetWinDiag()` counters: `SigData / SigSpace / SigContig / WaitData / WaitSpace / WaitContig`. |
| `SHM_DIAG_WRITERLOOP=1` | Enables `ShmFrameWriter.GetPhase3Waits()` count. Used to gauge the upside of `SHM_SAW_WRITERLOOP`. |
| `SHM_DIAG_HOPTIMING=1` | Enables `ShmGrpcStream.GetHopDiag()` measurement of reader-thread → user-thread channel hop latency. |
| `SHM_EVENTFD_DIAG=1` | Enables `LinuxDataSegWaker.GetDiag()` + `LinuxEventfdRingSync.GetSignalDiag()` + request-kind counts. Used to attribute time inside the Linux eventfd wake path. |

## Performance experiments (opt-in)

| Variable | Default | Effect |
|---|---|---|
| `SHM_CHANNEL_INLINE` | unset | When set to `1`, lets the `Channel<InboundFrame>` slow-path schedule its continuation **synchronously on the reader thread**, skipping the .NET `ThreadPool` dispatch (~10–15 µs/RT on Windows). Auto-disabled when strict-fair mode is active (multi-frame DATA + sync continuations + LazyChainRos sync-pull could self-deadlock the reader). |

## Internal tuning knobs (advanced)

These are read-once-at-load knobs that override otherwise-baked-in defaults. Setting them is normally not necessary; they exist for benchmark tuning or repro of niche regressions.

| Variable | Default | Effect |
|---|---|---|
| `SHM_INITIAL_WINDOW` | `33554432` (32 MiB) | Initial per-stream HTTP/2 send window (= per-stream `InFlow` limit and `WindowUpdateBatchThreshold` denominator). Capped to `int.MaxValue`. Smaller values force more frequent WU emissions; larger values reduce WU overhead but risk OOM if the peer is slow. |
| `SHM_WRITER_SPIN_ITERATIONS` | `3000` (= `SpinIterationsDefault`, capped at `SpinIterationsMax=10000`) | Spin budget for the WriterLoop before falling through to a kernel wait. Increase to trade CPU for tighter ping-pong latency under low-contention bench workloads; decrease to give CPU back to other work. |
| `SHM_RECEIVE_STRIPER` | unset (= enabled) | **Opt-OUT** knob. Set to `0` to disable the per-connection `ReceiveStriper` stripe-thread dispatch (fan-out across N work threads). Default-on because the striper is the only way to keep ~1000+ concurrent streams unblocked when one stream's user code stalls. Disable for single-stream micro-benchmarks where the striper hop is measurable. |
| `SHM_LPM_ASSERT_LOG` | unset | Path of a file to append `[SHM_LPM_ASSERT] ...` diagnostic lines to. When unset, the assertions still throw + log to stderr; setting a path adds file-based persistence for postmortem analysis. Used to catch the rare "declared=0, payload all-zero" framer corruption signature. |
| `SHM_DIAG_STRIPER=1` | Enables `ReceiveStriper.GetDiagCounters()` per-stripe enqueue + dispatch counters. Used to verify even fan-out across stripes when investigating tail-latency outliers. |

## Strict-fair benchmark mode (`--fair` in ringbench)

These together opt-into a benchmark-only mode where the SHM
transport mirrors TCP / UDS gRPC's HTTP/2 *wire-format* constraints, so
a reviewer can compare against TCP/UDS on equal terms.

| Variable | Set by `--fair` to | Effect |
|---|---|---|
| `SHM_FAIR_MAX_FRAME` | `16384` | Caps the per-DATA-frame payload at the HTTP/2 spec default `SETTINGS_MAX_FRAME_SIZE`. Large messages are split into multiple H2 DATA frames, the same way TCP/UDS gRPC would. Also gates off the `Channel<InboundFrame>` inline-continuation fast-path (`SHM_CHANNEL_INLINE`) and the server's HEADERS+DATA+Trailers coalescing so each frame is dispatched separately like TCP/UDS. |
| `SHM_DISABLE_POOLED_DESER` | `1` | Suppresses the `IPooledDeserializer` fast-path on the client so the client falls back to the stock `Grpc.Net.Client` buffered codec (which TCP/UDS use). Removes one allocation-pool advantage SHM has by default. |

> ℹ️ **Interop note.** `--fair` is interop-compatible with peers in any
> mode (default or `--fair`). Per-stream HTTP/2 flow control is active
> on both sides via `InFlow` / `TrInFlow` and a `WindowUpdateBatchThreshold`-
> driven drip path, so there is no FC mismatch to deadlock on.
> Connection-level FC remains elided in all modes (the ring's `WaitForSpace`
> provides connection-wide back-pressure).

## Production default summary

With no environment variables set, the transport runs:

- **No spin wait** on Windows (matches Linux eventfd protocol).
- **Futex-based** wake on Linux (eventfd path is opt-in via
  `SHM_EVENTFD_WAKE`).
- **Per-stream HTTP/2 flow control active** with a 32 MiB initial
  window (`InFlow` / `TrInFlow` + WU emission at limit/4). Connection-
  level FC remains elided — the ring's `WaitForSpace` provides
  connection-wide back-pressure.
- **No diagnostic instrumentation** in the hot path.
- **`ThreadPool`-dispatched** Channel continuations (cross-process
  fairness across many streams).
- **Pooled deserialization** on the client (peak GC reduction).
- **`ReceiveStriper` enabled** (opt-out via `SHM_RECEIVE_STRIPER=0`).

If a benchmark or smoke run misbehaves, the first thing to check is
whether any of the above variables are leaking in from the shell — many
of them visibly change steady-state behaviour.
