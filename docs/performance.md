# Performance analysis: Clast vs SqlClient

Reference machine: i9-12900K (8 P-cores with hyper-threading = CPUs 0-15,
8 E-cores = CPUs 16-23), SQL Server 2025 on the same machine, 1M rows x 17
columns (`bench_types_1000000`). Analysis date: 2026-10-17.

## Summary

- With the server session's placement controlled, Clast reads about 35%
  faster than SqlClient using typed getters (456 vs 703 ms with full TLS,
  399 vs 628 ms with login-only TLS), and as fast as SqlClient calling
  `Read()` without getting any values.
- Ingest is server-bound: the server's CPU time is about 95% of wall time
  for both drivers. Without TABLOCK they tie (about 2.2 s); with TABLOCK
  Clast is 5-15% faster.
- Clast uses about a third of SqlClient's client CPU, for both reads and
  ingest.
- Earlier results that showed Clast "slightly slower" came from where the
  server ran each session, not from the drivers.
- UTF-8 transcoding is not a factor. All decoding costs about 95 ms of
  client CPU per 1M rows. String columns are about 2% of the reading
  thread's time, which is spent mostly waiting for the server.

## Why earlier comparisons were misleading

SQL Server keeps each connection's requests on one scheduler. This machine
has 24 schedulers in two soft-NUMA nodes, split into even and odd CPUs. An
idle server gives each new connection the first scheduler of the next node,
so every connection's requests run on either CPU 16 (an E-core) or CPU 7 (a
P-core). Which one depends only on how many connections were opened before.

The two CPUs behave very differently:

- **CPU 7 shares work with a log writer.** SQL Server's log writer threads
  run on hidden schedulers bound to CPUs 3, 5, 7 and 9. A bulk load on CPU 7
  generates log on the same logical CPU that writes it, and takes about 2x as
  long (about 4.2 s vs 2.1 s without TABLOCK, 2.4 s vs 1.2 s with it). This
  happened for both drivers.
- **CPU 7's sibling is often the client.** CPUs 6 and 7 share a physical
  core, and the unpinned client thread often ran on CPUs 4-7.
- **Reads drift.** On a fixed CPU, single reads still range by about ±25%.
  Server elapsed time always equals wall time, so this is the server's own
  speed varying.

The BenchmarkDotNet suite runs each case in its own process, after a fixed
sequence of setup connections. So a driver could land on the slow CPU in
every iteration of its case. With unpinned placement:

- read medians differed by up to 2x between connections of the same driver;
- for the same driver and query, the server spent 344 ms of CPU on one
  connection and 630 ms on another.

The benchmarks now honor `MSSQL_BENCH_SERVER_CPU`
(`benchmarks/.../ServerPlacement.cs`). With it set, they reopen
connections until the measured session lands on that CPU. Pin the client
with BenchmarkDotNet's `--affinity`.

## Method

`dotnet run -c Release --project benchmarks/Clast.Drivers.SqlServer.Benchmarks -- --analyze read|ingest|transport|sessions [iterations]`
runs interleaved iterations of every case and records the following for each
run:

- wall time;
- client process CPU time;
- the session's server CPU and elapsed time (`sys.dm_exec_sessions`);
- packets sent (`sys.dm_exec_connections`);
- the session's waits (`sys.dm_exec_session_wait_stats`);
- the CPU the server ran the request on, sampled every 5 ms from
  `sys.dm_exec_requests`.

These modes need SQL Server's DMVs and refuse to run against a warehouse;
`--analyze client` (below) compares the drivers there.

`--analyze profile N full|login` is a steady loop for `dotnet-trace`.
`--analyze encode [iterations]` measures bulk-load encoding alone, without a
TLS, socket or server cost (see Ingest encoders).

Read cases include "drain" (Clast reads the token stream without decoding
values) and "SqlClient skip" (`Read()` without getting values). These are
each driver's transport floor.

## Reads (server on CPU 16, client on CPUs 0-3; medians of 16)

| case | wall | client CPU | server CPU | server waits |
|---|---|---|---|---|
| Clast decode, full TLS | 579 | 203 | 430 | none |
| Clast drain, full TLS | 562 | 125 | 438 | none |
| SqlClient typed, full, 8 KB packets (default) | 823 | 656 | 539 | ASYNC_NETWORK_IO 76 |
| SqlClient typed, full, 16 KB packets | 745 | 602 | 500 | ASYNC_NETWORK_IO 108 |
| SqlClient skip, full, 16 KB packets | 518 | 172 | 430 | none |
| Clast decode, login-only TLS | 500 | 172 | 414 | none |
| Clast drain, login-only TLS | 507 | 78 | 422 | none |
| SqlClient typed, login-only, 8 KB | 743 | 609 | 406 | ASYNC_NETWORK_IO 116 |
| SqlClient skip, login-only, 16 KB | 492 | 141 | 375 | none |

Times are in ms.

- **Clast is server-bound.** The server never waits on Clast, and draining
  is no faster than decoding.
- **SqlClient with typed getters is partly client-bound.** The server
  waits on the network (`ASYNC_NETWORK_IO`) for 76-155 ms of each read
  while SqlClient materializes values.
- **Packet size matters.** Clast asks for 32767 bytes; the server grants
  16384 with TLS and 32767 without. With 8000-byte packets, the
  default for SqlClient, Clast reads about 25% slower.
- **The transport address doesn't matter.** IPv6 loopback, 127.0.0.1 and
  `localhost` all gave the same results within noise.

## Client profile (`dotnet-trace`, sampled thread time, reading thread)

| | login-only TLS | full TLS |
|---|---|---|
| Blocked in socket receive | 81% | 63% |
| TLS decrypt and copy | - | ~8% |
| Decoding (all columns, including Arrow buffer management) | ~17% | ~20% |
| ...of which string columns (incl. UTF-16 to UTF-8) | ~2% | ~2% |

## Ingest (server on CPU 16, client on CPUs 0-3; medians of 6)

| case | wall | client CPU | server CPU |
|---|---|---|---|
| Clast, no TABLOCK | 2197 | 344 | 2078 |
| SqlBulkCopy, no TABLOCK | 2209 | 1008 | 2008 |
| Clast, TABLOCK | 1237 | 352 | 1180 |
| SqlBulkCopy, TABLOCK | 1475 | 984 | 1360 |
| SqlBulkCopy, TABLOCK, 16 KB packets | 1296 | 969 | 1210 |

Times are in ms. Minimums were within 10% of medians. Packet size does not
matter here. Both drivers spend about 95% of wall time waiting for the
server's CPU, so a faster client can't help.

### Ingest encoders

`--analyze encode [iterations]` measures bulk-load encoding alone: the
table's batches, through the encoders the driver uses for it, into a packet
writer over `Stream.Null`. Encoding was most of ingest's client CPU, 797
Mcycles (259 ms) per million rows against about 330 ms of client CPU for the
whole ingest. Each value went through two or three `ArrowBuffer` spans
(`Values`, `IsNull`, `GetBytes`), each a virtual
`MemoryManager<byte>.GetSpan` call for Arrow's native buffers. On top of
that came a delegate (integers), type tests (floats, temporal types), a
`stackalloc`, and an Int128 division (decimals).

The encoders now pin each column's buffers once per batch, read values and
the validity bitmap through pointers, and write each value with one bounds
check (`PacketWriter.GetSpan`/`Advance`). Integers are generic over the
source type, sources are resolved at `Bind`, the decimal limit is computed
once, and nvarchar is transcoded straight into the packet.

| per million rows (server CPU 12, client CPUs 0-3) | before | after |
|---|---|---|
| Encoding, 17 columns | 797 Mcycles, 259 ms | 238 Mcycles, 76 ms |
| Fixed-width value (int, float, bit) | 7-11 ns | 3.2-3.8 ns |
| decimal / datetime2 / nvarchar value | 11 / 21 / 22 ns | 5.1 / 6.2 / 9.3 ns |
| Ingest, client CPU, managed | 328 ms | 188 ms |
| Ingest, client CPU, NativeAOT from Python | 375 ms | 211 ms |

Wall time is unchanged (about 2.4 s, server-bound). What remains of the
client CPU is mostly TLS and the socket, not encoding.

## BenchmarkDotNet, placement-controlled

`MSSQL_BENCH_SERVER_CPU=16`, `--affinity 15`, 15 iterations:

| | Clast | SqlClient | Go |
|---|---|---|---|
| Read, full TLS | 456 ± 47 ms | 703 ± 102 ms | 3877 ms* |
| Read, login-only TLS | 399 ± 38 ms | 628 ± 37 ms | 3718 ms* |
| Ingest, no TABLOCK | 2.67 ± 0.58 s | 3.00 ± 0.71 s | 3.63 s |
| Ingest, TABLOCK | 1.87 ± 0.31 s | 1.75 ± 0.38 s | 3.64 s (no TABLOCK option) |

\* Pinning the process to 4 CPUs also limits the Go runtime's threads.
Unpinned, Go reads in about 1.7 s.

Ingest varies more between consecutive iterations under BenchmarkDotNet
(tempdb log and truncation state). The interleaved harness above is the
better measure there.

## NativeAOT vs the Go driver, from Python (medians, min-max)

`benchmarks/python/aot_vs_go.py` loads both native drivers, the NativeAOT
build of this one (`adbc_driver_sqlserver.dll`) and the Go driver 1.7.0,
into one Python process through `adbc_driver_manager`. Both hand results
over through the Arrow C stream interface, so the host favours neither:
`fetch_arrow_table()` collects the batches without copying them. The test is
the same 1M x 17 table with full TLS, the server session on CPU 12 (where
sessions land now; CPU 16 no longer appears), 8 interleaved runs, and client
CPU over all of the process's threads. Ingest appends the table as this
driver reads it into an empty table, with tinyint widened to int16 for both,
since the Go driver can't ingest uint8.

| 1M rows | Wall ms | Client CPU ms |
|---|---|---|
| Read, NativeAOT | 444 (421-618) | 281 (250-344) |
| Read, Go | 1657 (1622-1710) | 3766 (3609-4172) |
| Ingest, NativeAOT | 2023 (1892-2374) | 375 (219-562) |
| Ingest, Go | 3135 (3069-3212) | 3766 (3453-3984) |
| Load + connect + `SELECT 1`, new process, NativeAOT | 34 (31-35) | |
| Load + connect + `SELECT 1`, new process, Go | 29 (29-33) | |

The NativeAOT driver reads 3.7x as fast as the Go driver, at 1/13 of the
client CPU, and ingests 1.5x as fast at 1/10. Confining the client to CPUs
0-3 changes neither driver's figures. The cold start is about the same, a
few milliseconds apart. Go returns the same 64K-row batches, but it maps
datetimeoffset to a UTC timestamp, dropping the offset, and marks datetime2
as UTC.

For comparison, `--analyze client` in the same placement measures the
managed (JIT) build in a .NET process: 551 ms and 211 ms CPU to read, and
312 ms CPU to ingest. NativeAOT costs about a third more client CPU (no
tiered compilation or dynamic PGO), but no wall time, since reads and
ingest are server-bound.

The same harness also shows that hosting the Go driver inside .NET (through
`CAdbcDriverImporter`, as `ReadBenchmarks` does) slows it: 2.2 s to read,
and 6.7 s when the process is confined to CPUs 0-3, against 1.7 s from
Python either way. Go numbers from the .NET benchmarks above understate it.

## Fabric Warehouse (remote; medians, min-max)

Against a Fabric Warehouse over the internet, with an Entra access token for
all three drivers, from `--analyze client`. The 1M-row table has the same
values in the warehouse's column types (`clast_bench_types_1000000`). Client
CPU is this process's, all threads.

| Read, 1M rows (5 runs) | Wall ms | Client CPU ms |
|---|---|---|
| SqlClient (typed getters) | 30718 (29840-33273) | 656 (547-672) |
| Go 1.7.0 | 36493 (35763-37933) | 6109 (5875-6562) |
| Clast | 31680 (29884-34843) | 281 (250-375) |

| Ingest, 1M rows (2 runs) | Wall ms | Client CPU ms |
|---|---|---|
| SqlBulkCopy | 20509 (20406-20612) | 969 (953-984) |
| Clast | 15325 (15226-15423) | 250 (250-250) |

Reads are bound by the service (see below): all three take about 30
seconds, and the differences are within run-to-run variation, except Go's.
The client cost is not: Clast uses 0.43x SqlClient's CPU and 0.05x Go's.
Ingest is 25% faster than SqlBulkCopy, at a quarter of the CPU. The Go driver
bulk-loads a warehouse only by staging through Azure storage
(`mssql.ingest.storage_account`), so it has no ingest figure here.

### Where a warehouse read's time goes

Measured on 2026-10-18 from this machine (wired 1 Gbps, about 30-38 ms from
the Fabric gateway), reading `clast_bench_types_1000000` with Clast; the
probe counts bytes on the physical adapter (`netstat -e` totals the virtual
adapters too and reports about 4x).

- **Volume:** 158 MB arrives for 1M rows, about 148 bytes per row, as the
  TDS packet trace also shows. Nothing is inflated on the wire.
- **Server work:** the first batch arrives after about 3 s. Fabric's
  `queryinsights.exec_requests_history` shows 1-2 s of CPU per full read,
  and a scan with aggregates over the table takes about 1 s. The server's
  elapsed time matches the client's: it waits on sending, not computing.
- **Transfer:** the other 25 s or so, at 5.2-6.6 MB/s (about 50 Mbit/s).

That rate is a limit of the warehouse's result path (its frontend or the
gateway in front of it), not of the client or its network:

- **Not the link:** single downloads from unrelated hosts reach 46-60 MB/s
  here, receive-window auto-tuning is on, and the client uses about 0.3 s of
  CPU per read.
- **Not the packet size or TLS:** 4 KB, 8 KB and 16 KB packets (Fabric grants
  16290 bytes when asked for 32767) and strict (TDS 8) encryption all give
  the same rate.
- **Not the storage or the row shape:** generated rows that read no table
  stream at the same 6-8 MB/s, and so does `id` alone (9 MB at about
  6 MB/s): the limit is bytes per second, not rows.
- **Shared across a client's connections:** connections go through a
  regional gateway to one warehouse frontend. Two concurrent connections
  share the 6.5 MB/s one gets alone, whether they read halves of the table
  or all of it each; they take turns rather than run side by side, whichever
  gateway instance they pass through. Whether the share is per client or per
  capacity can't be told from outside.

Parallel reads, which open one session per stream, raise the total but far
less than linearly:

| `adbc.mssql.parallel_reads` | 1 | 2 | 4 | 8 | 16 |
|---|---|---|---|---|---|
| Wall s | 27.0 | 25.7 | 19.5 | 14.9 | 13.2 |
| MB/s | 6.6 | 7.0 | 9.6 | 12.4 | 13.6 |

So against a warehouse, a large read is best made with `parallel_reads` of 8
to 16 on an integer column, which about halves the time. No driver can make
one stream faster; for bulk extraction, reading the table's Parquet files
from OneLake avoids this path. Measuring from an Azure VM in the region
would show whether the limit is distance or the service itself.

## What would make single-stream reads faster

Nothing on the client: the client is idle 63-81% of the time. The
remaining levers are on the server side:

- **Packet size:** already the largest the server grants.
- **Encryption:** login-only TLS saves about 15%.
- **More schedulers:** parallel reads use several server schedulers at once,
  about 5x faster with 16 sessions.

A read-ahead thread to overlap decryption with decoding would not help
against this server.

## Other findings

- Integrated authentication failed when the server was given as the
  machine's LAN address (an earlier note blamed `127.0.0.1`, which works).
  That address resolves to `host.localdomain`, and without a domain NTLM's
  loopback check rejects that SPN. SqlClient and the ODBC driver fail the
  same way. Fixed: for a local server the driver now retries with the machine
  name, and `adbc.mssql.server_spn` sets the SPN explicitly.
- After login, reads still go through the `PreLoginTlsStream` wrapper, and
  `SslStream` copies decrypted data into our buffer: about 3% of thread
  time. This is unavoidable with `SslStream` and not worth pursuing while
  reads are server-bound.
