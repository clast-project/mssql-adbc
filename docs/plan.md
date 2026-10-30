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
| Connect | TCP, named pipes/LocalDB, TLS, SQL auth, integrated (SSPI) | Entra (access token and discovery since done), failover, MARS, pooling |
| ADBC | ExecuteQuery, ExecuteUpdate, BulkIngest, GetInfo, GetTableSchema, version 1.1 | GetObjects, transactions, prepare/bind, partitions, statistics (all since done; see below) |
| Types | bit, tinyint–bigint, real/float, decimal/numeric, (n)varchar(n), varbinary(n), date, datetime2, datetimeoffset (`arrow.timestamp_with_offset`), uniqueidentifier (`arrow.uuid`) | `(max)`/PLP, datetime/smalldatetime/time, money, sql_variant (`arrow.parquet.variant`), xml, json, vector, hierarchyid (all since done; see Type mapping) |

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
- ADBC C# referenced as the `Apache.Arrow.Adbc` NuGet package (1.0, the first
  release with ADBC 1.1).
- net10.0 first, plus netstandard2.0 for .NET Framework and other hosts. The
  netstandard2.0 build fills in what it lacks (`Compat/`: 128-bit integers,
  span overloads of Stream and Encoding, UTF-8 transcoding, and other newer
  BCL members, mostly as C# 14 extension members so call sites read the
  same) and calls Windows SSPI directly for integrated authentication.

## Server strings (`adbc.mssql.server`)

| Form | Meaning |
|---|---|
| `host`, `host,port`, `tcp:host[,port]` | TCP; `.`, `(local)` and `localhost` mean this machine |
| `host\instance` | named instance, resolved with the SQL Server Browser (SSRP, UDP 1434): TCP if the instance listens on it, else its named pipe |
| `tcp:host\instance` | named instance over TCP only |
| `host\instance,port` | the given port; no browser lookup |
| `host\MSSQLSERVER` | the default instance; no browser lookup |
| `np:host`, `np:host\instance` | the standard pipe (`sql\query` or `MSSQL$instance\sql\query`) |
| `np:\host\pipe\name` | an explicit pipe |
| `(localdb)\name`, `(localdb)\.\shared` | LocalDB (Windows): started on demand through SqlUserInstance.dll, then reached over its pipe. LocalDB has no TLS, so encryption defaults to login-only unless set explicitly |

The browser is asked at all of the host's addresses at once, again each
second until the connect timeout, and the result is reused by the
connection's helper sessions (parallel reads). A failed connection to a
resolved port (or LocalDB pipe) clears it, so a restarted instance is found
again.

## Routing

A server can answer a login with a routing ENVCHANGE (type 20): Azure SQL's
redirect connection policy (the default inside Azure), read-only routing and
failover groups use it. The driver closes that session and logs in again at
the host and port named, with the same credentials, database and options,
validating TLS against the new host name and sending `host,port` as the
LOGIN7 server name, as SqlClient does. It follows up to 10 routes within the
original connect timeout. Parallel-read helper sessions start from the
original server and are routed in turn.

Enhanced routing is requested too (feature extension 0x0F). A server that
acknowledges it can route with ENVCHANGE 21, which also names the database:
the login is repeated at that server and database, while the connection's
original settings (and so parallel-read sessions) keep the database asked
for. As in SqlClient, an enhanced route from a server that did not
acknowledge the feature is ignored and the session stays where it logged in;
one without a database is a protocol error.

## Connect retry

A login that fails with one of Azure's transient errors (SqlClient's list:
4060, 10928, 10929, 40020, 40143, 40166, 40197, 40501, 40540, 40613, 42108,
42109) is retried from the start, routing included, after
`adbc.mssql.connect_retry_interval` seconds (1–60, default 10), up to
`adbc.mssql.connect_retry_count` times (0–255), as long as the wait leaves
some of the connect timeout. With the 15-second default timeout that allows
one retry, as in SqlClient. Other failures, including network errors, are
not retried.

The count defaults by host name as SqlClient sets it: 5 for Synapse
serverless (`-ondemand`) and Fabric/Power BI warehouses, 2 for Azure SQL
(`.database.windows.net` and the sovereign and Fabric equivalents), 1 for
Synapse dedicated pools (SqlClient's general default). Elsewhere it is 0,
where SqlClient uses 1: none of the errors but 4060 come from other servers,
and there 4060 ("cannot open database") is nearly always a wrong name that a
retry would only report 10 seconds later. SqlClient also raises an explicit
count of 1 for Azure hosts; here an explicit count is used as given.

## Session recovery

Logins request session recovery (feature extension 0x01). When the server
acknowledges it, the session tracks what the server needs to recreate it:
the initial states from the acknowledgement, their changes from SESSIONSTATE
tokens (opaque values, each marked recoverable or not), and the database,
language and collation from ENVCHANGE.

Before each request, the connection checks the idle session's socket. A
readable socket with nothing outstanding means the server closed it (a
failover, a gateway reset, a killed session). If the session is recoverable,
the connection logs in again from its original settings, so routing happens
again. That login carries the recovery data, the server recreates the
session's database, language and SET options, and the new session replaces
the old one. The isolation level the driver set carries over, and stale
parallel-read helper sessions are dropped. Transient login errors during the
reconnect get the usual connect retry.

It doesn't recover when a transaction was open, when the server reported
unrecoverable state (temporary tables, for example), when the server did not
acknowledge the feature, or with `adbc.mssql.session_recovery=false`. Then
the next request fails with IOError saying why. A connection lost during a
request is not retried: that request fails, and the next one recovers. Named
pipes give no early sign of a closed connection, so a pipe session fails on
its next request instead.

## Synapse and Fabric

The analytics engines speak TDS but accept a subset of T-SQL. The driver
asks a session for `SERVERPROPERTY('EngineEdition')` once, when something
depends on it, and exposes it as `adbc.mssql.engine_edition` (connection
GetOption). Edition 6 is a Synapse dedicated SQL pool; 11 is a Synapse
serverless SQL pool or a Fabric Warehouse / SQL analytics endpoint (which
the edition does not tell apart); 12, SQL database in Fabric, is Azure SQL
and needs nothing different.

| | Synapse dedicated (6) | Fabric Warehouse, Synapse serverless (11) |
|---|---|---|
| Isolation level | Fixed: read uncommitted, or read committed (snapshot) with READ_COMMITTED_SNAPSHOT on. Reported from `sys.databases`; setting another level fails with NotImplemented, and no SET is sent (the server rejects it, error 104409) | Fixed: snapshot (the server ignores SET, and Fabric documents it as unsupported). Same handling |
| GetObjects, GetStatistics | Only the current database: no cross-database queries | All databases, as elsewhere |
| Approximate statistics | None: no `sys.dm_db_stats_histogram` | None, likewise |
| Ingest, created tables | `WITH (HEAP)` when a column is (max), which columnstore (the default) cannot hold; json as nvarchar(max) | Delta Parquet types: strings and json as varchar(max) in the warehouse's UTF-8 collation, tinyint as smallint, fixed binary as varbinary(n), datetime2/time at most 6 digits, time-zone-aware timestamps as UTC datetime2; datetimeoffset, sql_variant and vector fail |

Everywhere, the current database's catalog views are named with two-part
names (`sys.objects`, not `[db].sys.objects`), and ingest's replace mode
uses `IF OBJECT_ID(...) IS NOT NULL DROP TABLE` (Synapse has no
`DROP TABLE IF EXISTS`).

Fabric Warehouse is verified against a real warehouse by `WarehouseTests`,
which run when `MSSQL_WAREHOUSE_CONFIG_FILE` names a JSON file of driver
options and an `accessTokenFile`. They cover the fixed isolation level,
ingest in every mode with the type mapping (UTF-8 varchar strings included;
Fabric's bulk load is in preview and needs Entra authentication),
`sp_tablecollations_100`, GetTableSchema, GetObjects, statistics,
transactions, bound parameters, GetParameterSchema and parallel reads.
Two things it took to connect at all:

- The gateway routes to `host\instance`. As in SqlClient, the connection
  goes to the host at the port given, and LOGIN7 names the whole route.
- The routed node refuses a login whose PRELOGIN has no TRACEID ("Couldn't
  complete the operation due to a system update", error 18456). The driver
  now sends one in every PRELOGIN: a new connection ID and activity ID.

Fabric also reports twice the rows of a bulk load, so ingest now returns
the rows it sent, as SqlBulkCopy does.

`adbc.mssql.ingest.batch_size` (statement option, default 0 for one batch)
splits ingest into an `INSERT BULK` and BulkLoad message per that many rows,
like `SqlBulkCopy.BatchSize`. Under autocommit each batch commits on its own,
so a failure keeps the batches before it; in a transaction they all go
together. Verified against SQL Server and a Fabric Warehouse
(`WarehouseTests.IngestsInBatches`: several batches on one session, a
rollback, and an aborted batch after a committed one). On Fabric each batch
costs time: 1M rows took 16.5 s in one batch and 23.5 s in batches of
100,000 (`--analyze client 3 ingest 0,100000`; SqlClient's `BatchSize` is
no faster), so batch only for the autocommit recovery it gives.

Synapse is not verified against a real pool; the tests play it with a fake
TDS server, from Microsoft's documentation. Untested there: GetParameterSchema
on a dedicated pool (needs database compatibility level 20 or higher), and
DDL inside a transaction on a dedicated pool (unsupported, so creating a
table with autocommit off fails there).

## Encryption

| `adbc.mssql.encrypt` | Protocol | TLS |
|---|---|---|
| `optional` | TDS 7.4 | login only (TLS 1.2 inside PRELOGIN) |
| `mandatory` (default) | TDS 7.4 | whole session; SQL Server uses TLS 1.2 inside PRELOGIN |
| `strict` | TDS 8 | TLS before any TDS traffic (ALPN `tds/8.0`), TLS 1.3 where both ends have it; the certificate is always validated unless trusted explicitly |

Verified against SQL Server 2025 with a certificate from
`scripts/New-SqlServerTlsCertificate.ps1`. Under strict the PRELOGIN
ENCRYPTION option says "not supported" (TLS is already in place), as
SqlClient sends it, and TLS session resumption is off: Windows caches sessions
per host regardless of TDS version, so a strict connection would otherwise
resume a TDS 7.x connection's TLS 1.2 session.

## Microsoft Entra authentication

- `adbc.mssql.access_token`: an Entra access token, sent in the LOGIN7
  FEDAUTH feature extension (security-token library) after PRELOGIN
  FEDAUTHREQUIRED; it replaces username/password/integrated security. It is
  never sent over a connection without TLS (login-only TLS is enough, as the
  token is inside LOGIN7). Acquiring and refreshing tokens is the caller's job.
- Discovery: `GetOption("adbc.mssql.entra.authority")` and
  `GetOption("adbc.mssql.entra.audience")` on the database (no credentials
  needed) or a connection. The driver runs a probe handshake with the MSAL
  workflow, reads the server's FEDAUTHINFO (STS URL and SPN) and hangs up
  without logging in; the answer is cached. Servers without Entra report
  NotImplemented. A stopgap until ADBC 1.2 can return such metadata directly.
- Tested against a fake TDS server (real TLS 1.2 inside PRELOGIN, as SQL
  Server does) and, for the unconfigured case, the real server.

## Type mapping

| SQL Server | Arrow | Notes |
|---|---|---|
| tinyint, smallint, int, bigint | uint8, int16, int32, int64 | |
| real, float | float, double | |
| bit | bool | |
| decimal(p,s), numeric(p,s) | decimal128(p,s) | |
| money, smallmoney | decimal128(19,4), decimal128(10,4) | |
| char, varchar, nchar, nvarchar (incl. max), text, ntext | utf8 | varchar/text decoded from the column's code page; UTF-8 collations pass through |
| xml | utf8 | Arrow has no XML type. Ingested as the UTF-8 text in varbinary(max), which the server parses as XML, so an `encoding="utf-8"` declaration holds |
| json | `arrow.json` (utf8) | native transport (feature extension 0x0D): UTF-8 on the wire. Ingested as nvarchar(max), which the server converts |
| binary, varbinary (incl. max), image, timestamp/rowversion | binary | |
| hierarchyid | utf8 | text form (`/1/2.5/-3/`), converted on the client (`Arrow/HierarchyId.cs`). `adbc.mssql.hierarchyid=binary` keeps the serialized bytes, which are shorter and sort in tree order where the text does not. Ingest takes either |
| geometry, geography, other CLR types | binary | serialized bytes; type name in `MSSQL:type`. Ingested as varbinary. Spatial as WKB is #2 |
| vector(n) | fixed_size_list&lt;float32&gt;[n] | native transport (0x0E); float32 elements only. Ingest also accepts list&lt;float32&gt; of the right length |
| date | date32 | |
| time(s) | time64[ns] | |
| datetime2(s), datetime, smalldatetime | timestamp[us] | |
| datetimeoffset(s) | `arrow.timestamp_with_offset` (us) | |
| uniqueidentifier | `arrow.uuid` | RFC 4122 byte order |
| sql_variant | `arrow.parquet.variant` (unshredded) | per value: tinyint/smallint → int16, int → int32, bigint → int64, real/float → float/double, bit → boolean, decimal/numeric → decimal4/8/16 by precision, smallmoney/money → decimal8/16 (scale 4), date → date, time → time (us), datetime/smalldatetime/datetime2 → timestamp_ntz (us), datetimeoffset → timestamp (us, UTC; the offset is not kept), uniqueidentifier → uuid, character types → string, binary types → binary |

`adbc.mssql.string_encoding=utf16` returns what would be utf8 above (all but
json) as `clast.utf16` instead: little-endian UTF-16 in binary, offsets in
bytes (`Utf16StringType`). nvarchar is copied as the server sends it, with no
transcoding, unpaired surrogates included; varchar is decoded from its code
page, or from UTF-8, into UTF-16. It is for consumers whose strings are UTF-16
(Analysis Services is the first). It applies to the results the application
asks for (queries, partitions, ExecuteSchema, GetTableSchema); catalog
functions keep their standard utf8 schemas, and the driver reads its own
queries as utf8. Ingest and bound parameters take `clast.utf16` wherever they
take utf8, whatever the option: into nvarchar it is sent as it is, into
varchar it is transcoded (unpaired surrogates become U+FFFD or `?`), into xml
it is sent behind a UTF-16 byte order mark, and as a parameter it is nvarchar.

Every field carries the SQL Server type in `MSSQL:type` metadata. Ingest
into sql_variant takes variants and maps primitives back (int8/int16 →
smallint, decimal4/8/16 → decimal(9/18/38), timestamps → datetime2(6) or
datetimeoffset(6) at +00:00, string → nvarchar, binary → varbinary); objects
and arrays are rejected. Bound parameters accept the same types (json as
nvarchar). Create mode makes sql_variant, json and vector(n) columns from
those Arrow types, and xml and hierarchyid columns from string (or, for
hierarchyid, binary) fields whose `MSSQL:type` says so, as the driver reads
them; not on Synapse or Fabric, which have neither type.

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
| Read-only | `adbc.connection.readonly` = read-only application intent (LOGIN7 fReadOnlyIntent, as `ApplicationIntent=ReadOnly`), set before connecting: AG listeners and Azure SQL read scale-out route to a readable secondary. Not enforced by a server without replicas, since SQL Server has no read-only session mode |
| Bind / Prepare | bound rows are `sp_executesql` RPC calls with typed parameters (`@name` per field, `@p1`… if unnamed). ExecuteUpdate batches up to 1000 calls (≤1 MB) per message; ExecuteQuery runs a call per row and concatenates the result sets. Prepare only validates |
| ExecuteSchema | `sp_describe_first_result_set`, with parameters declared from the bound schema or from `sp_describe_undeclared_parameters`. It fails where the shape can't be known (dynamic SQL without `WITH RESULT SETS`, a temp table in a multi-statement batch, branches that return different shapes), where `SET FMTONLY ON` would return a shape that may be wrong, and run a procedure that turns it off. Names and nullability come from its rows; Arrow types from `SET FMTONLY ON` on a `SELECT` of a typed NULL per column, so they come from TDS metadata as query results' do. A server without it (error 2812, or 15871 for an unsupported procedure, as the only error) gets `SET FMTONLY ON` on the query; the same errors from inside the described batch come with 11529 after them and are thrown |
| GetParameterSchema | `sp_describe_undeclared_parameters` |
| Statistics | approximate: table row counts (`sys.partitions`) and column distinct/null counts from each column's statistics histogram (`sys.dm_db_stats_histogram`); exact: row counts with `COUNT_BIG(*)`. No driver-specific statistics |
| Incremental execution | `adbc.statement.exec.incremental`: the partitions are range queries, all known at once and run only when read, so the first ExecutePartitions returns them all and the next an empty set (which ends that execution). Progress (`adbc.statement.exec.progress`, `max_progress`) is not reported |
| Substrait | `SubstraitPlan` translated to T-SQL, with a published Substrait dialect (`adbc.mssql.substrait.dialect`); see [substrait.md](substrait.md) |

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
- Read-ahead (`adbc.mssql.read_ahead=N`, 0 to 16, default 0): a serial
  stream decodes on a background thread into a bounded channel, up to N
  batches ahead of the caller (it waits for room before decoding the next),
  so decoding overlaps the caller's work on the previous batch. Batches stay
  in order; an error follows the batches decoded before it, and a cancel
  takes effect at once. The connection stays busy until the stream ends or
  is disposed.
- Bound parameters run one execution per row, with the session idle between
  them, where an attention has nothing to cancel. So while the rows last, the
  session is in an operation (`TdsSession.BeginOperation`): a cancel then also
  keeps later executions from being sent at all (an attention right behind a
  request can be missed by the server, which then runs it to the end), and
  cancels the token passed to the bound stream's reads. This covers serial,
  read-ahead and ExecuteUpdate alike, including the executions ExecuteQuery
  runs before the first result set. A bound stream that ignores the token
  can still hold up a cancel until its read returns. The token is cancelled
  with no driver lock held, since its callbacks are the caller's code.
  ExecuteUpdate reads the rows while sending a message of several calls; a
  failure or cancel there ends the message with the IGNORE bit, as
  ingestion does, so the server discards the part it has received.
  1M rows locally, with a consumer that spends a fixed time per row (off /
  N=1 / N=2): 2 µs/row 2.84 / 2.06 / 2.06 s (consumer-bound), 0.5 µs/row
  1.25 / 0.86 / 0.83 s, no consumer work 0.93 / 0.84 / 0.80 s (within noise).
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
