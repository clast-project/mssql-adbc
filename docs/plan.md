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
