# Substrait

The driver executes [Substrait](https://substrait.io/) plans: set a serialized
`substrait.Plan` as the statement's `SubstraitPlan` (ADBC's
`AdbcStatementSetSubstraitPlan`) instead of SQL, and it is translated to T-SQL
and run. Plans follow the Substrait v0.107 protobuf messages and standard
extensions; older producers' legacy fields are accepted too.

Constructs that can't be translated with the same semantics are rejected with
`SubstraitTranslationException` (InvalidArgument) rather than approximated.
Unknown fields in relations and expressions are rejected too, since ignoring
them could change a query's meaning. Three differences are accepted instead,
since rejecting them would mean rejecting every string or binary comparison;
the dialect records them under `metadata.semantics`:

- Strings are UTF-16: a character outside the Basic Multilingual Plane, such
  as an emoji, counts as two in lengths and positions, and sorts below U+E000.
- Comparisons ignore trailing spaces: `'a' = 'a '`, and the two group together.
- Binary comparisons ignore trailing zero bytes: `0x01 = 0x0100`.

| Piece | Where |
|---|---|
| Dialect: what translates | [`sqlserver_dialect.yaml`](../src/Clast.Drivers.SqlServer/Substrait/Dialect/sqlserver_dialect.yaml), in the Substrait dialect format |
| What the dialect format can't say | [substrait-dialect-gaps.md](substrait-dialect-gaps.md) |
| Protobuf decoding | `Substrait/ProtoReader.cs`, `SubstraitPlanDecoder.cs`, `SubstraitModel.cs` (derived from the Kusto ADBC driver) |
| T-SQL generation | `Substrait/TSqlEmitter.cs` (relations, expressions), `TSqlFunctionCatalog.cs` (functions) |
| Entry point | `SqlServerSubstrait.ToSql(plan)` and `.DialectYaml` |

## Options

| Option | Where | Meaning |
|---|---|---|
| `adbc.mssql.substrait.collation` | statement | Collation for string values. Default `Latin1_General_100_BIN2_UTF8`: binary, case-sensitive comparison, sorting and grouping, the closest to Substrait's semantics (but on UTF-16 code units, and ignoring trailing spaces, as above). Empty: the columns' own collations, which lets the server use indexes on them but usually ignores case |
| `adbc.mssql.substrait.sql` | statement, read-only | The T-SQL the current plan translates to |
| `adbc.mssql.substrait.dialect` | database or connection, read-only | The dialect YAML |

GetInfo reports `VendorSubstrait` true and the Substrait version (0.107.0).
`ExecuteQuery`, `ExecuteSchema`, `ExecuteUpdate`, `Prepare` (which checks that
the plan translates) and partitioned and parallel reads all take plans; bound
parameters don't, since plans have none.

## Translation

Each relation becomes part of a SELECT. A relation on top of another is added
to the same SELECT while T-SQL's clause order allows it (a projection over a
filter over a read is one SELECT) and otherwise wraps it in a derived table:
a filter over a TOP, an aggregate over an aggregate. A sort that must survive
a derived table is carried through hidden columns holding its keys. Table
aliases are numbered across the whole query, so expressions stay valid inside
correlated subqueries.

Notable mappings:

- **Booleans.** T-SQL conditions are not values. A boolean expression is
  written as a condition where one is needed (WHERE, ON, CASE WHEN) and as a
  three-valued `bit` elsewhere: `CASE WHEN p THEN 1 WHEN NOT p THEN 0 END`.
- **Strings.** Every string value is given the query's collation (above), at
  column references, literals and casts; everything computed from them
  inherits it. Select lists name bare columns without it, so that xml or json
  columns, which have no collation, can still be read.
- **Nulls in sorts.** SQL Server sorts nulls lowest; nulls-last ascending and
  nulls-first descending orders get a `CASE WHEN x IS NULL` key first.
- **Joins.** Any join condition works. Semi and anti joins are `EXISTS` and
  `NOT EXISTS`; mark joins a three-valued `CASE` over two `EXISTS`. A filter on a
  join input moves to the outer WHERE or the ON clause, whichever keeps its
  meaning. An outer join's null-supplying input with computed columns is a
  derived table, so those columns are null for a missing row.
- **Aggregates.** Grouping sets are `GROUPING SETS`, with the set index column
  from `GROUPING_ID`. A measure's filter passes null for excluded rows
  (`SUM(CASE WHEN f THEN x END)`), which aggregates skip. Grouping by a
  constant or a subquery computes the keys in a derived table first.
- **Set operations.** UNION, EXCEPT and INTERSECT, with the ALL variants
  (which T-SQL lacks) computed by numbering duplicate rows with
  `ROW_NUMBER() OVER (PARTITION BY every column)`.
- **Fetch.** `TOP (n)`, `TOP (n) WITH TIES`, or `OFFSET ... FETCH`.
- **Errors.** Integer overflow, division by zero and float domain errors raise
  errors in SQL Server, which the dialect declares (`overflow: ERROR`,
  `on_division_by_zero: ERROR`); NULL behaviors are delivered with `NULLIF`.
  Casts are `CAST`, or `TRY_CAST` for RETURN_NULL.
- **Dates and times.** `datetime2` and `datetimeoffset` with at most 100 ns.
  Interval literals become `DATEADD` calls; there is no interval type. Time
  zones must have a fixed offset (SQL Server knows Windows zone names only),
  and the session time zone is UTC. Day-of-week parts don't depend on
  `SET DATEFIRST`.

Not supported: window functions and window relations, lateral, single and
hash/merge joins, `MINUS_MULTISET`, row-value `IN` subqueries, `UNIQUE`,
nested types (struct, list, map), dynamic parameters, user-defined types and
type variations declared in the plan, writes and DDL. The dialect lists
everything that is.

## Tests

- `SubstraitTranslationTests`: hand-built plans, checked as T-SQL text and
  with Microsoft's T-SQL parser (ScriptDom), and rejections.
- `DataFusionFixtureTests`: 66 plans DataFusion 55 produced from SQL
  (`Fixtures/DataFusion`, regenerated by `scripts/gen_datafusion_fixtures.py`).
  Each translation must match a reviewed `.tsql` snapshot
  (`MSSQL_UPDATE_SNAPSHOTS=1` rewrites them), and, given a server, return what
  DataFusion returned for the SQL over the same rows (`data.json`). 56
  translate; 52 match DataFusion. The other four are listed in the test with
  the reason: DataFusion's plans for `NOT IN`, and for grouping sets, whose
  set index column it places before the measures where Substrait places it
  after.
- `SubstraitExecutionTests`: hand-built plans run on a server, for what
  DataFusion doesn't produce: mark joins, every set operation, WITH TIES,
  ordered aggregates, correlated subqueries, date parts, intervals, casts and
  the error-or-null function options.
- `DialectTests`: the dialect validates against Substrait's dialect schema
  (`Fixtures/Substrait/dialect_schema.yaml`), and lists exactly the functions,
  implementations and options of the translator's catalog.

## Differences from the Kusto driver

The plan decoder and the dialect conventions come from the
[Kusto ADBC driver](https://github.com/CurtHagenlocher/kusto_adbc). The
decoder was extended for what T-SQL can do and KQL can't: virtual tables,
grouping sets, ordered aggregates, WITH TIES, subqueries with outer
references, mark joins and the remaining set operations. There is no partial
pushdown: a plan translates entirely or is rejected.
