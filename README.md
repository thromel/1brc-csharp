# 1BRC C# solution

This is a standalone C# solution for the [One Billion Row Challenge](https://github.com/gunnarmorling/1brc). It targets `net10.0`, uses NativeAOT when published, and is tuned first for a 10-core Apple Silicon machine.

The current result is not a straight port of the fastest x64 submissions. Those lean heavily on AVX2 or wider vectors. This solver came from a different question: what does a serious C# implementation look like on macOS ARM64 when the full-size run exposes a VM/page-fault bottleneck that the bounded parser benchmarks do not show?

## What it does

The input format is the original 1BRC contract:

```text
station-name;temperature
```

The solver preserves:

- arbitrary UTF-8 station names from 1 to 100 bytes
- up to 10,000 unique stations
- min, mean, and max by integer tenths
- ordinal station-name sorting
- the expected one-decimal mean rounding

The implementation stays close to the metal: unsafe byte parsing, native-memory hash tables, line-aligned work partitioning, and final decoding only after aggregation.

## Quick start

Build the release DLL:

```sh
dotnet build OneBrc.CSharp/OneBrc.CSharp.csproj -c Release
```

Run against a file:

```sh
./calculate_average_csharp.sh /path/to/measurements.txt
```

If no path is passed, the script reads `measurements.txt` from the current directory.

For the fastest local path, publish NativeAOT first:

```sh
./publish_aot.sh
./calculate_average_csharp.sh /path/to/measurements.txt
```

To force a specific SDK or runtime, set `DOTNET`:

```sh
DOTNET=/path/to/dotnet ./publish_aot.sh
DOTNET=/path/to/dotnet ./calculate_average_csharp.sh /path/to/measurements.txt
```

`calculate_average_csharp.sh` prefers the current-platform NativeAOT binary when it exists and is newer than the source. Otherwise it falls back to the release JIT DLL.

## Validation

From the cloned upstream 1BRC repository, use the wrapper script:

```sh
./test.sh csharp
```

When touching I/O behavior, validate both input paths on macOS:

```sh
BRC_IO=mmap ./test.sh csharp
BRC_IO=pread ./test.sh csharp
```

The project was developed with a correctness-first loop: official fixtures first, generated-output parity second, paired timings last.

## Runtime controls

The default runtime policy lives in `RuntimeOptions`.

```sh
BRC_THREADS=8 ./calculate_average_csharp.sh /path/to/measurements.txt
BRC_IO=mmap ./calculate_average_csharp.sh /path/to/measurements.txt
BRC_IO=pread ./calculate_average_csharp.sh /path/to/measurements.txt
```

- `BRC_THREADS` overrides worker count.
- `BRC_IO=mmap` forces the memory-mapped path.
- `BRC_IO=pread` forces the macOS native `pread` path.

By default, smaller files use `mmap`. On macOS, files at least 8 GiB use the `pread` pipeline because paired full 1B runs on this host showed the memory-mapped path spending heavily in page faults and kernel work.

The current 10-core Apple Silicon defaults are:

- mmap: 13 workers for files at least 1 GiB, 9 workers for files at least 64 MiB
- pread: 8 workers for files at least 1 GiB, 9 workers for files at least 64 MiB

These are local heuristics, not universal constants. On a different machine, start with `Environment.ProcessorCount`, sweep `BRC_THREADS`, and only then promote a new default.

## Architecture

The code is organized around a few internal components:

- `Program` handles process input and output.
- `OneBrcSolver` selects the input strategy.
- `RuntimeOptions` owns environment variables and machine-tuned defaults.
- `RangePartitioner` and `WorkerScheduler` handle the mmap path.
- `PReadRangePartitioner`, `PReadWorkerScheduler`, and `NativePRead` handle the macOS chunked-read path.
- `MeasurementParser` owns row parsing and temperature parsing.
- `StationKey`, `StationTable`, and `StationEntry` own aggregation.
- `ResultFormatter` decodes station names and writes sorted output.

See [ARCHITECTURE.md](ARCHITECTURE.md) for the component map, porting notes, and validation policy.

## What we learned

The first good version looked like a normal high-performance parser problem: split the mapped file on line boundaries, parse byte ranges in parallel, keep station names as pointers, and merge native tables before formatting.

That worked well on bounded 100M-row runs. It did not explain the full 1B run. On the full file, the memory-mapped path spent a large amount of time in system calls and page faults. The useful rewrite was not SIMD temperature parsing. It was a macOS `pread` pipeline that streams line-aligned ranges through reusable native buffers and copies only unique station names.

On the local full-size canonical file, the shift was large. A first parity run measured mmap/pread wall time at `16.227s`/`4.297s`, with system time at `26.026s`/`3.902s` and major faults at `777206`/`76`. Five paired warm full-1B runs kept the same shape: `pread` won `5/5`, with median candidate-minus-baseline deltas of `-11.785s` wall, `-26.445s` system time, and `-842044` major faults. The bounded 100M benchmark still favors mmap, so the promoted default is workload-size aware instead of forcing `pread` everywhere.

Several tempting ideas did not survive measurement:

- SIMD temperature parsing regressed because the temperatures live at scattered addresses.
- 64-byte SIMD row framing regressed because mask extraction and row bookkeeping cost more than the scalar delimiter scan saved.
- station front caches and known-station direct paths added work to a table that already averaged near one probe on canonical data.
- control-byte table metadata helped too little on high-cardinality data and hurt canonical data.
- dynamic microshards reduced some CPU counters but did not robustly improve full-1B wall time.

The table is no longer the obvious battlefield for this machine. The current frontier is profiling the full-size `pread` path with better hardware-counter or Time Profiler access, then making source changes only if the profile gives a clear target.

Full experiment history is in [RESEARCH_NOTES.md](RESEARCH_NOTES.md).

## Porting notes

This code should be portable in shape, but not in tuning.

- ARM64 CRC32C is used when available; `StationKey` has a scalar fallback.
- The macOS `pread` implementation is isolated in `NativePRead`.
- A Linux or Windows read path should be added behind a separate native wrapper and selected from `RuntimeOptions`.
- x64 AVX2 or AVX-512 parser work should be a separate parser/key path, not mixed into the current ARM64 path.

Before changing defaults on another machine, run official fixtures, compare output against the current promoted build on generated data, and benchmark with paired runs. Single lucky timings are not enough.
