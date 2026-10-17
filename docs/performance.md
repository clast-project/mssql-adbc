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

`--analyze profile N full|login` is a steady loop for `dotnet-trace`.

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

- Integrated authentication fails when the server is given as an IP
  address. Clast fails for `127.0.0.1`, where SqlClient succeeds, and both
  fail for the machine's LAN address ("target principal name is
  incorrect"). The Kerberos SPN is built from a reverse lookup; the loopback
  case needs to fall back like SqlClient's native SNI does.
- After login, reads still go through the `PreLoginTlsStream` wrapper, and
  `SslStream` copies decrypted data into our buffer: about 3% of thread
  time. This is unavoidable with `SslStream` and not worth pursuing while
  reads are server-bound.
