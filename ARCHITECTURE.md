# Architecture and Porting Guide

This solver is intentionally small, unsafe, and performance-oriented. The maintainability goal is not to hide that work behind broad abstractions; it is to keep each hardware-sensitive decision isolated, documented, and easy to retune.

## Design Goals

- Preserve the full 1BRC contract: arbitrary UTF-8 station names from 1 to 100 bytes, up to 10,000 stations, integer-tenth aggregation, ordinal output ordering, and the expected one-decimal mean rounding.
- Keep hot row parsing allocation-free.
- Store station names as byte slices until final formatting.
- Make platform-specific choices explicit and overridable.
- Reject optimizations unless they pass official fixtures and output parity first, then paired benchmark evidence.

## Component Map

- `Program` owns process input/output.
- `OneBrcSolver` selects the input strategy and returns formatted output.
- `RuntimeOptions` owns environment variables, worker-count defaults, and input-strategy policy.
- `RangePartitioner` and `WorkerScheduler` handle the memory-mapped path.
- `PReadRangePartitioner`, `PReadWorkerScheduler`, and `NativePRead` handle the macOS native `pread` path.
- `MeasurementParser` owns row framing, station-key creation, and integer temperature parsing.
- `StationKey`, `StationTable`, and `StationEntry` own aggregation storage.
- `ResultFormatter` decodes and sorts final station results.
- `PReadPhaseProfile` contains optional compile-time profiling support behind `BRC_PHASE_PROFILE`.

## Input Strategies

The default path is selected by `RuntimeOptions`.

- `mmap`: default for smaller files and non-macOS machines. It maps the input once, partitions the mapped bytes on line boundaries, and stores station-name pointers into the mapped file.
- `pread`: default on macOS for files at least 8 GiB. It streams each worker range through reusable 16 MiB native buffers and copies only unique station names into native arenas.

Use `BRC_IO=mmap` or `BRC_IO=pread` to force a strategy during experiments. `pread` is currently implemented for macOS through `libSystem.B.dylib`.

## Runtime Tuning

`RuntimeOptions` is the only place that should contain default runtime policy.

- `BRC_THREADS=<n>` overrides the worker count.
- `BRC_IO=mmap` forces the memory-mapped path.
- `BRC_IO=pread` forces the native `pread` path on macOS.

The 10-core Apple Silicon defaults are evidence-based local heuristics:

- mmap: 13 workers for files at least 1 GiB, 9 workers for files at least 64 MiB.
- pread: 8 workers for files at least 1 GiB, 9 workers for files at least 64 MiB.

Other machines should start with `Environment.ProcessorCount`, then sweep worker counts with `BRC_THREADS`.

## Performance Invariants

Several choices are deliberate and should not be refactored away casually:

- Temperatures are stored as integer tenths, not floating point.
- The station table is fixed at 32,768 buckets because the 1BRC contract caps unique stations at 10,000.
- Hash identity is not trusted alone. Entries compare length, first word, last word, and full bytes for names longer than 16 bytes.
- The mmap path does not copy station names because the mapped file outlives all table operations.
- The pread path does copy unique station names because chunk buffers are reused.
- `StationKey` uses ARM64 CRC32C when available and a scalar mixer elsewhere.
- The parser has a padded fast path and bounded tail path; both are needed for correctness on shard ends and unusual fixture rows.

## Porting To Another Architecture

Porting should be incremental:

1. Build and run fixtures without changing algorithms:

   ```sh
   dotnet build OneBrc.CSharp/OneBrc.CSharp.csproj -c Release
   cd ../work/1brc
   ./test.sh csharp
   ```

2. Publish NativeAOT for the target runtime:

   ```sh
   ./publish_aot.sh
   ```

3. Verify hardware feature assumptions:

   - `StationKey` already falls back when ARM CRC32C is unavailable.
   - x64 AVX2 or AVX-512 work should be added as a separate parser/key path, not mixed into the ARM64 path.
   - OS-specific I/O should stay behind a dedicated native wrapper like `NativePRead`.

4. Sweep worker counts:

   ```sh
   BRC_THREADS=6 ./calculate_average_csharp.sh /path/to/measurements.txt
   BRC_THREADS=8 ./calculate_average_csharp.sh /path/to/measurements.txt
   BRC_THREADS=10 ./calculate_average_csharp.sh /path/to/measurements.txt
   ```

5. Compare input strategies where supported:

   ```sh
   BRC_IO=mmap ./calculate_average_csharp.sh /path/to/measurements.txt
   BRC_IO=pread ./calculate_average_csharp.sh /path/to/measurements.txt
   ```

6. Promote new defaults only by editing `RuntimeOptions`, and record the benchmark evidence in `RESEARCH_NOTES.md`.

## Adding A New OS I/O Path

Do not branch native calls inside parser code. Add a small wrapper and a scheduler boundary:

- create a native wrapper similar to `NativePRead`
- create a range partitioner if boundary discovery differs
- reuse `MeasurementParser.ParseRangeInto`
- use `StationTable(copyNames: true)` when buffers are temporary
- keep the selection policy inside `RuntimeOptions`

## Validation Policy

Every meaningful change should pass:

```sh
dotnet build OneBrc.CSharp/OneBrc.CSharp.csproj -c Release
cd ../work/1brc
./test.sh csharp
```

For I/O strategy changes, also force both paths on the official fixtures where the platform supports them:

```sh
BRC_IO=mmap ./test.sh csharp
BRC_IO=pread ./test.sh csharp
```

For performance promotion, compare byte-for-byte output against the current production build on generated canonical and high-cardinality files before running paired timings.
