# Clast.Drivers.SqlServer: plan

An ADBC 1.1 driver for SQL Server (and, eventually, any TDS server), written in C#
for .NET 10, with performance as the primary goal. Main workloads: analytical
reads and bulk ingestion.

## Approach: selective fork of SqlClient's TDS layer

SqlClient's parser (`TdsParser.cs`, ~14k lines) is tightly coupled to
`SqlConnectionInternal`, `SqlCommand`, `SqlDataReader` and Always Encrypted,
uses a snapshot-replay async model, and materialises every value into a
`SqlBuffer`, which is the per-value overhead this driver exists to remove. A
literal file fork drags in ~60k lines. Instead:

- **Copy and trim**: managed SNI transport (TCP, named pipes, TDS 7.x
  in-prelogin TLS and TDS 8 strict TLS, LocalDB pipe resolution), `TdsEnums`,
  collations, type-metadata tables.
- **Lift into new types**: prelogin, Login7 and feature extensions, SSPI via
  `System.Net.Security.NegotiateAuthentication`, env-change and error handling.
- **Rewrite**: the token loop, column decoders and the bulk-load encoder, using
  SqlClient as the byte-layout reference.

Only the MIT-licensed managed SNI is used; the native SNI runtime is closed
source. Files derived from SqlClient keep their .NET Foundation MIT headers and
are listed in `THIRD-PARTY-NOTICES.md`.

## Architecture

```
Clast.Drivers.SqlServer
├─ Transport/  TcpTransport, NamedPipeTransport, TLS (7.x in-prelogin + TDS 8), LocalDB
├─ Protocol/   PacketReader/PacketWriter (pooled, whole-packet), PreLogin, Login7 + FeatureExt,
│              SSPI, TokenReader, errors/env-change
├─ Arrow/      ColumnDecoder per TDS type → Arrow buffers; BulkEncoder Arrow → TDS rows
└─ (root)      SqlServerDriver / Database / Connection / Statement
```

**Reads.** On COLMETADATA, build one decoder per column that writes directly
into native Arrow buffers (no per-value objects); the NBCROW null bitmap feeds
validity directly. An async read-ahead task fills a pipe of whole packets while
decoding runs synchronously over fully buffered data, so I/O and decode overlap
without SqlClient's snapshot replay. `nvarchar` is transcoded UTF-16 → UTF-8
with the vectorised transcoder; `varchar` with a UTF-8 collation passes through;
other code pages are decoded. Batches are cut by row count and byte budget.

**Ingest.** `INSERT BULK` (optional `TABLOCK`), writing TDS rows straight from
Arrow columns into pooled packet buffers. Modes create / append / replace /
create_append; create modes derive `CREATE TABLE` from the Arrow schema.

## Prototype scope

| Area | Prototype | Deferred |
|---|---|---|
| Connect | TCP, named pipes/LocalDB, TLS, SQL auth, integrated (SSPI) | Entra, failover, MARS, pooling |
| ADBC | ExecuteQuery, ExecuteUpdate, BulkIngest, GetInfo, GetTableSchema, version 1.1 | GetObjects, transactions, prepare/bind, partitions, statistics (all since done; see below) |
| Types | bit, tinyint–bigint, real/float, decimal/numeric, (n)varchar(n), varbinary(n), date, datetime2, datetimeoffset (`arrow.timestamp_with_offset`), uniqueidentifier (`arrow.uuid`) | `(max)`/PLP, datetime/smalldatetime/time, money, sql_variant (`arrow.parquet.variant`), xml, json, vector, hierarchyid |

## Milestones

- **M0** Scaffold, build settings, benchmark harness with SqlClient (typed
  getters) and Go driver (`CAdbcDriverImporter`) baselines.
- **M1** Transport, prelogin, TLS, login; `SELECT 1` round-trips its tokens.
- **M2** Token reader and prototype-type decoders; `ExecuteQuery` returns an
  `IArrowArrayStream`; results verified against SqlClient.
- **M3** Bulk encoder and `BulkIngest`; round-trip tests; ingest benchmarks
  (SqlBulkCopy and Go driver baselines).
- **M4** Read and ingest benchmarks against both baselines; profile and tune.
- **M5+** Deferred types, remaining ADBC 1.1, Linux/macOS, netstandard2.0,
  telemetry.

## Decisions

- Namespace `Clast.Drivers.SqlServer`; Apache-2.0; Clast strong-name key.
- Option prefix `adbc.mssql.*` (renamable later); `username`/`password` use the
  standard ADBC keys.
- ADBC C# referenced from a local checkout via `AdbcCSharpRoot` in
  `Directory.Build.props`; expected to become a submodule.
- net10.0 only for now; netstandard2.0 later, without shaping today's design.

## ADBC 1.1 coverage

| Area | Implementation |
|---|---|
| Cancellation | TDS attention; see Findings |
| GetInfo | vendor name/version (from LOGINACK), SQL/Substrait support, driver name/version, Arrow version, ADBC version |
| GetTableTypes | `TABLE`, `VIEW` |
| GetTableSchema | `SELECT TOP 0` and the same type mapping as query results |
| GetObjects | databases → schemas → tables/views → columns (XDBC/ODBC attributes) and constraints (PK, UNIQUE, FK with referenced columns, CHECK), from each database's catalog views, one batch per database |
| Transactions | autocommit off = `IMPLICIT_TRANSACTIONS ON`; Commit/Rollback; isolation levels incl. snapshot (helper sessions follow) |
| Options | current catalog (USE), current schema (read-only), `GetOption` on database, connection and statement |
| Bind / Prepare | bound rows are `sp_executesql` RPC calls with typed parameters (`@name` per field, `@p1`… if unnamed). ExecuteUpdate batches up to 1000 calls (≤1 MB) per message; ExecuteQuery runs a call per row and concatenates the result sets. Prepare only validates |
| ExecuteSchema | `SET FMTONLY ON`, with parameters declared from the bound schema or from `sp_describe_undeclared_parameters` |
| GetParameterSchema | `sp_describe_undeclared_parameters` |
| Statistics | approximate: table row counts (`sys.partitions`) and column distinct/null counts from each column's statistics histogram (`sys.dm_db_stats_histogram`); exact: row counts with `COUNT_BIG(*)`. No driver-specific statistics |
| Not done | Substrait, read-only connections, incremental execution |

## Findings (M4, local SQL Server 2025, 1M rows × 17 columns)

- Reads are bound by the server and transport, not by decoding: draining the
  token stream without decoding takes as long as full Arrow decoding
  (`ReadDiagnostics`). Faster decoders will not move end-to-end read time
  against this server; packet size and encryption do.
- TLS sessions are granted at most 16384-byte packets. Login-only encryption
  (`adbc.mssql.encrypt=optional`) allows 32767 and reads ~25% faster.
- Arrow buffers come from `Apache.Arrow.Memory.NativeBuffer<,>` (public in
  Apache.Arrow 23): aligned native memory, ownership moved into the
  `ArrowBuffer` without a copy, freed when the batch is disposed. A 1M-row
  read allocates ~4 MB of managed memory and causes no GCs.
- Ingest with TABLOCK is ~1.4× faster than SqlBulkCopy and ~2.9× faster than
  the Go driver; without TABLOCK the gap is smaller and server-dominated.
- Partitioned reads (`adbc.mssql.partition.column` / `.count`, integer
  ranges, one descriptor per range, read on separate connections) scale from
  ~780 ms (1 partition) to ~165 ms (16 partitions) on a clustered-index
  table. NULL keys go in a separate UNION ALL arm of the first partition so
  every range stays seekable.
- Encryption, measured separately (1M rows, full / login-only): this driver
  ~870 / ~530 ms, SqlClient ~720 / ~730 ms, Go ~1730 / ~1680 ms. With full
  TLS this driver and SqlClient are within run-to-run noise, which varies by
  connection (std. dev. up to ~190 ms); login-only lets this driver use
  32767-byte packets. Full encryption stays the default.
- Single-stream parallel reads (`adbc.mssql.parallel_reads=N` with a
  partition column): ExecuteQuery plans 4 partitions per worker, N workers
  read them on the connection's session plus pooled helper sessions, and
  batches are merged unordered through a bounded channel. 1M rows: ~700 ms
  (1) → 312 (2) → 261 (4) → 153 (8) → 135 ms (16). Over-partitioning keeps
  one slow session from setting the pace (4 workers: std. dev. 225 → 18 ms).
  Falls back to a single stream inside a transaction; local temp tables are
  not visible to helper sessions.
- Cancellation uses the TDS attention signal. `Cancel` on the connection or
  statement (or a cancelled token on any async call or
  `ReadNextRecordBatchAsync`) sends an attention while the response is being
  read, or right after the request if it is still being sent; the reader
  skips to the server's acknowledgment (a DONE with the ATTN bit, possibly in
  a later message) and throws `AdbcStatusCode.Cancelled`, and the session
  stays usable. A small lock orders the request state against `Cancel`, so
  an attention that races the end of a response is still acknowledged
  before the next request. Bulk-load messages cannot carry an attention, so
  ingestion polls for cancellation and ends the message with the IGNORE bit.
  Disposing an unfinished stream sends an attention instead of reading the
  rest of the result, so abandoning a huge result takes milliseconds.
  Parallel streams cancel every worker session.
- Next lever: read-ahead to overlap TLS decryption with decoding when
  decoding cost becomes visible.
