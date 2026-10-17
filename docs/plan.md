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
| ADBC | ExecuteQuery, ExecuteUpdate, BulkIngest, GetInfo, GetTableSchema, version 1.1 | GetObjects, transactions, prepare/bind, partitions, statistics |
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
- Next lever: read-ahead to overlap TLS decryption with decoding when
  decoding cost becomes visible.
