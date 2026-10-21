# Substrait Dialects: Gaps Found While Writing a T-SQL Dialect

This note describes what the Substrait dialect format
([`text/dialect_schema.yaml`](https://github.com/substrait-io/substrait/blob/main/text/dialect_schema.yaml),
as of Substrait v0.107) can and cannot express. It is based on writing a complete dialect for
a real consumer: an ADBC driver that translates Substrait plans into T-SQL for SQL Server 2025.
It is meant for Substrait and ADBC contributors evaluating whether a dialect file is enough
for a producer to generate plans that a consumer will accept *and* execute with the intended
semantics.

It is the second such exercise. The first was for an ADBC driver that translates Substrait
into Kusto Query Language
([kusto_adbc](https://github.com/CurtHagenlocher/kusto_adbc/blob/main/substrait-dialect-gaps.md)).
The two engines have little in common: one is a pipe-based log analytics language, the other
a SQL database with configurable collations. So the gaps both hit are unlikely to be quirks of
either. The tables below mark them.

**Short version:** the dialect format fits *what* a consumer supports: relations, join types,
set operations, subquery kinds, expression kinds, types with precision bounds, and functions by
extension, signature and option. It cannot describe four kinds of information a producer needs:

* *how* a consumer restricts those features (literal-only arguments, supported enumeration
  values, sub-features of relations);
* the *semantics* that decide results, above all how strings compare;
* that capabilities differ between versions and editions of one engine;
* how the consumer's own column types appear in Substrait.

For SQL Server the semantic gap is the most serious. String comparison depends on a
collation, which is chosen per column, per database and per expression, and Substrait has no
word for it. In practice the dialect check is necessary but not sufficient. The SQL Server
dialect records the missing information as free-form `metadata`, which no producer can act on
automatically.

Separately from the format, the ecosystem has its own problems. Producers omit extension URNs
and emit non-standard functions; the standard extensions have holes; and producers can disagree
with the specification. These limit what any dialect can achieve today.

---

## Background

The driver publishes `sqlserver_dialect.yaml` (in the driver's source,
`src/Clast.Drivers.SqlServer/Substrait/Dialect/`). It validates against the dialect schema and
is the contract for the translator:

* every function, implementation and option behavior it lists translates to T-SQL, and anything
  else is rejected rather than approximated;
* a unit test checks the dialect's function lists against the translator's function catalog, so
  the file cannot drift from the code;
* the translator is tested against 66 plans that DataFusion 55 produced from SQL, and 56 of them
  translate. Every translation must parse as T-SQL (with Microsoft's T-SQL parser) and match a
  reviewed snapshot;
* each translation runs on SQL Server 2025 over the same data DataFusion ran the SQL on,
  including null keys and values, and strings that differ only in case. 52 of the 56 return
  identical results. The four that don't are covered below: a null-aware anti join (gap 1), and
  three plans with grouping sets where DataFusion disagrees with the specification (see
  "Producers that disagree with the specification").

Hand-built plans cover what DataFusion doesn't produce: mark joins, every set operation, WITH
TIES, ordered aggregates, correlated subqueries, interval arithmetic and the error-or-null
function options.

The gaps below are things we needed to say about the SQL Server consumer and could not.

| # | Gap | Effect on producers | Severity | Also found for Kusto |
|---|-----|---------------------|----------|-------|
| 1 | No way to declare collation or other comparison semantics | Results depend on settings the producer can't see | High | Yes (ordering, nulls) |
| 2 | A dialect is static, but capabilities depend on version, edition and settings | Plans pass the check and fail on the server | High | No |
| 3 | Argument constraints (literal-only arguments) can't be declared | Intervals, separators, patterns, time zones fail | High | Yes |
| 4 | Supported enumeration values can't be declared | `extract` components, `median` EXACT/APPROXIMATE | High | Yes |
| 5 | Relation sub-features can't be declared | Duplicate grouping sets, WITH TIES with offset, ordered aggregates | Medium | Yes |
| 6 | `required_options`: unclear defaults, one entry per combination | Producers can't tell what happens without an option; entries multiply | Medium | Yes |
| 7 | Type value domains, result types and column type mappings | No NaN in float; decimal division types; tinyint, datetimeoffset | Medium | Yes |
| 8 | Cast support is all-or-nothing | Which casts work, and which change the value | Medium | Yes |
| 9 | Variadic `min: 0` is not allowed | `and()`/`or()` with no arguments can't be declared | Low | Yes |
| 10 | Consumer limits | CASE nesting depth, IN-list size, identifier length | Low | Yes |
| 11 | Named-table naming rules | Number and meaning of name parts, temporary tables | Low | Yes |
| 12 | No Substrait version in the dialect | Can't tell which proto/extension version is meant | Low | Yes |

---

## Gaps in the dialect format

### 1. Collation and other comparison semantics

Substrait has no notion of collation: two strings are equal when they are the same string. SQL
Server compares strings with a collation, which may be set per column, per database, per server
or per expression. The default for a new server, `SQL_Latin1_General_CP1_CI_AS`, ignores case,
so `'apple' = 'Apple'` is true, the two group together, and `DISTINCT` keeps one of them.
`LIKE`, `REPLACE`, `CHARINDEX`, `MIN` and `MAX` follow the collation too. Run as written, a plan
returns results that depend on how the database was set up.

The translator gives every string value a binary collation (`Latin1_General_100_BIN2_UTF8`).
That compares and sorts exactly and case-sensitively, which is Substrait's semantics. But the
choice has a cost the dialect can't mention: a collated column in a predicate can't use an
index seek. So the driver has a statement option to switch it off,
which brings the database's semantics back. **A producer cannot tell which of the two semantics
a dialect describes**, or ask for one.

Some differences are left even with a binary collation, and none can be declared:

* **Supplementary characters.** SQL Server's strings are UTF-16, and its binary collations
  work on code units. A character outside the Basic Multilingual Plane, such as an emoji,
  counts as two in `char_length`, `strpos` and `substring`, `reverse` splits it, and it sorts
  below U+E000 to U+FFFF instead of above them (all measured). The linguistic collations that
  count it as one character don't order by code point. No collation gives both.
* **Trailing spaces.** SQL Server pads the shorter string with spaces before comparing, under
  every collation, so `'a' = 'a '` is true and the two group together. (Measured; `LIKE` with a
  Unicode pattern is exact, and the translator uses one.)
* **Binary values** are padded with zero bytes the same way: `0x01 = 0x0100` is true.
* **Nulls sort lowest.** Substrait's sort directions say where nulls go, so this one is
  translatable (with an extra sort key), but a producer can't see that it costs anything.
* **Ordering of `uniqueidentifier`** compares the last six bytes first. Ordering comparisons on
  uuid are rejected; `equal` works.

The functions with a `case_sensitivity` option show the asymmetry. When a plan asks for
`CASE_INSENSITIVE` on `like`, the translator can deliver it, with a case-insensitive collation.
Even that is only approximate. A case-insensitive collation is linguistic, so it also ignores
characters such as the soft hyphen, and it treats fullwidth `Ａ` as `a` unless the collation is
also width-sensitive. Substrait doesn't say what `CASE_INSENSITIVE` means beyond case.
For `equal`, `lt`, grouping, sorting and set operations there is no option, so their
semantics are wholly the consumer's.

The Kusto dialect needed the same kind of statement for different facts: strings that can't
be null, `==` returning false for a null operand, ordinal string ordering, and where NaN sorts.

*Suggestion:* a `semantics` section with standardized keys that producers can check the way they
check `supported_execution_behavior`. For example `string_comparison: [CODEPOINT,
CASE_INSENSITIVE, ...]`, `trailing_space_padding: true`, `null_ordering`, `nan_ordering`,
`empty_string_is_null`. Better still, a way to *request* one of several semantics a consumer
offers, as plan-level options request a function behavior.

### 2. A dialect is static; capabilities are not

One ADBC driver for SQL Server connects to SQL Server 2016 through 2025, Azure SQL Database,
Fabric Warehouse and Azure Synapse. What it can run depends on the server:

* **Version.** `LEAST`/`GREATEST`, `IS [NOT] DISTINCT FROM`, `LTRIM`/`RTRIM` with a character
  set and `LEFT_SHIFT`/`RIGHT_SHIFT` need SQL Server 2022. `PRODUCT` and the `REGEXP_` functions
  need SQL Server 2025. The UTF-8 collations behind exact string semantics need 2019.
* **Database settings.** Some newer functions need a database compatibility level (`REGEXP_LIKE`
  needs 170). A database's default collation changes results (gap 1).
* **Edition.** Fabric Warehouse and Synapse accept a subset of T-SQL, with different type
  support. For example, a warehouse has no `datetimeoffset` columns.

The dialect format has one `name` and unconditional lists. The SQL Server dialect describes
SQL Server 2025 and lists the version requirements in `metadata`. A plan for an older server
passes the dialect check and fails when it runs.

*Suggestion:* either let consumers serve a dialect per connection (an ADBC API that returns
the dialect for *this* connection, which a driver can compute from the server version), or allow
conditions on entries, e.g. `requires: {system_version: ">= 16"}`. The first is simpler and
covers settings too.

### 3. Argument constraints

Many T-SQL constructs need literal arguments, and the dialect can't say so:

* **Intervals.** SQL Server has no interval type. `add(timestamp, interval_day)` is translated
  to `DATEADD` calls, which need the interval's parts as numbers, so the interval must be a
  literal.
* **Time zones.** SQL Server only knows Windows time zone names, so the time zone argument of
  `extract`, `assume_timezone` and `local_timestamp` must be a literal naming a zone with a fixed
  offset (`UTC`, `Etc/GMT+5`, `+05:30`). IANA zones with daylight saving can't be translated.
* **Separators and patterns.** The separator of `string_agg` must be a literal (a T-SQL rule).
  The pattern and replacement of the `regexp_` functions must be literals, so that the translator
  can check and rewrite them (gap "Under-specified standard functions").

Kusto needed the same for `like` patterns, trim characters and fetch counts. Extension YAML has
`constant: true` for value arguments, but that describes the *function*, not a consumer's
restriction of it, and none of the standard functions above set it.

*Possible extension:* per-implementation argument constraints in `dialect_function`, such as
`argument_constraints: [{index: 1, constant: true}]`, and `allowed_values` for string arguments
like time zones.

### 4. Enumeration argument values

Enumeration arguments are part of the signature (`req`), so `supported_impls: [req_req_date]`
says that `extract` with an indexing option and a date is supported, but not *which* components.
The translator supports YEAR, ISO_YEAR, US_YEAR, QUARTER, MONTH, DAY, DAY_OF_YEAR,
MONDAY_DAY_OF_WEEK, SUNDAY_DAY_OF_WEEK, ISO_WEEK, US_WEEK, HOUR through NANOSECOND, SUBSECOND and
UNIX_TIME. It doesn't support MONDAY_WEEK and SUNDAY_WEEK (whose numbering is unclear; see below),
PICOSECOND or TIMEZONE_OFFSET. Likewise:

* `extract_boolean`: IS_LEAP_YEAR, not IS_DST;
* `median(req, fp64)`: APPROXIMATE (`APPROX_PERCENTILE_CONT`) but not EXACT, because T-SQL's
  exact `PERCENTILE_CONT` is only a window function.

The Kusto translator hit the same: its set of `extract` components differs, and it too can only
deliver an approximate median.

*Possible extension:* `enum_values: {0: [YEAR, MONTH, ...]}` per implementation, or treating
enumeration values like option values.

### 5. Relation sub-features

Most relation entries carry only `relation` and `metadata`. Features inside a relation that a
consumer may or may not support can't be declared:

| Relation | Undeclarable feature | SQL Server |
|----------|---------------------|------------|
| AGGREGATE | number of grouping sets | any, but not the same set twice (`GROUPING_ID` must tell them apart) |
| AGGREGATE | aggregate `sorts` (ordered aggregates) | `string_agg` only (`WITHIN GROUP`) |
| AGGREGATE | `AggregationInvocation.DISTINCT` per function | count, sum, avg, min, max, std_dev, variance |
| AGGREGATE | subqueries in measures and grouping keys | not allowed by T-SQL |
| TOP_N | `FetchMode.WITH_TIES` | supported, but not with an offset |
| SORT | sort keys of a given type | not uuid (see gap 1) |
| JOIN | `LEFT_SINGLE`, `RIGHT_SINGLE` | not supported |
| SUBQUERY | number of `IN` needles | one (T-SQL has no row-value `IN`) |
| SUBQUERY | `SET_PREDICATE` operations | `EXISTS`, not `UNIQUE` |

Join conditions, the Kusto dialect's biggest gap, are no problem for SQL, which accepts any
condition. But the null-aware anti join problem remains, and neither the plan nor the dialect
can express it (substrait-io/substrait#325). DataFusion emits `LEFT_ANTI` for `NOT IN`. The
translator gives it Substrait's semantics (`NOT EXISTS`), which differ when the subquery
returns a null. The differential test hit exactly this case for both consumers: on data where
SQL's `NOT IN` returns no rows, the translation returns three (for Kusto, four).

*Possible extension:* sub-feature fields on relation entries, e.g.
`aggregate: {max_grouping_sets: ..., ordered_aggregates: [string_agg]}`,
`top_n: {with_ties: WITHOUT_OFFSET}`, and on `dialect_function`, `supported_invocations: [ALL, DISTINCT]`.

### 6. `required_options`: defaults and combinations

The schema says `required_options` lists "the exact list of options that must be used". The
SQL Server dialect reads each entry as a behavior the consumer delivers, as the Kusto dialect
does, and states that reading in a comment. Writing the SQL Server dialect showed two more
problems.

* **What happens without the option.** Substrait lets a consumer choose when a plan omits an
  option. For most functions the translator's choice is the first listed behavior. But a
  `like` without `case_sensitivity` follows the query's collation, so it is case-sensitive or
  not depending on a statement option (gap 1). The dialect can't say what the default is, so a
  producer that needs a particular behavior must always ask for it.
* **Independent options multiply.** Each entry has one value per option. `divide` on integers
  can deliver `on_division_by_zero` ERROR or NULL (dividing by `NULLIF(divisor, 0)`), and
  `modulus` TRUNCATE or FLOOR with ERROR or NULL, so these take four entries each. The
  `regexp_` functions have three independent options (`case_sensitivity`, `multiline`,
  `dotall`). Supporting every combination would take eight entries per function; the dialect
  lists only the two with `multiline` and `dotall` disabled.

*Suggestion:* `supported_options: {option: [values...]}` with a marked default, e.g.
`{case_sensitivity: {values: [CASE_SENSITIVE, CASE_INSENSITIVE], default: CONSUMER}}`, and
validation of option names and values against the referenced extension. The upstream test file
`dialects/tests/functions_test.yaml` still validates with `notation: INFIX` inside
`required_options`.

### 7. Type value domains, result types and column type mappings

* **Value domains.** `FP64` is declared like any other type, but SQL Server's `float` can't
  hold NaN or infinity. NaN literals are rejected, `is_nan` is false for every value, and float
  overflow raises an error instead of giving infinity, an error no option of `add` can declare.
  For temporal types, `max_precision: 7` can be said, but not the range (years 1 to 9999),
  as for Kusto.
* **Result types.** Substrait derives the result type of decimal division as
  `decimal<P1 - S1 + P2 + S, S>` with `S = max(6, S1 + P2 + 1)`. SQL Server uses `S2` where
  Substrait uses `P2`, so `decimal(5,2) / decimal(7,3)` is `decimal(20,10)` in Substrait and
  `decimal(16,10)` in SQL Server (measured). The translator casts to Substrait's type, but the
  quotient was computed at SQL Server's precision, which can lose digits for large operands. A
  dialect can't declare a different derivation. Similarly, `ABS`, `SIGN` and `CEILING` widen
  `smallint` and `real` results, which the translator casts back.
* **Column type mappings.** `supported_as_column` says whether a type can be a column. It
  can't say how the consumer's own column types appear to a producer. Some examples from this
  driver:
  * `tinyint` is unsigned (0 to 255) and has no Substrait type. The standard extensions now have
    `unsigned_integers.yaml`, but its types are user-defined types that no producer we know of
    emits for a `uint8` Arrow column.
  * `datetimeoffset` keeps a time zone offset that Substrait's `precision_timestamp_tz` (an
    instant) doesn't. The driver returns it as an Arrow extension type, so a producer reading the
    table's schema doesn't see a Substrait type at all.
  * `money` appears as `decimal(19,4)`.
  * `xml`, `json` and `hierarchyid` appear as strings, but can't be compared or passed to string
    functions.

  Kusto has the same problem with `decimal`, `guid` and `dynamic`.

*Suggestion:* `min_value`/`max_value` and `special_values: false` on types; an optional
`column_type_mapping` from system type names to the Substrait type a producer will see, with
the operations that type allows.

### 8. Casts

`cast_expression.failure_options` declares RETURN_NULL and THROW_EXCEPTION. SQL Server has both
(`TRY_CAST` and `CAST`), which is the good news. But the dialect can't say which source-target
pairs are supported, or what a cast does to the value:

* casts to string from floating-point, binary, date and time values are rejected, because SQL
  Server formats them differently from other engines (`CAST(1e10 AS nvarchar)` is `1e+010`);
* booleans become `'true'`/`'false'` and uuids lower-case text, which differ from what
  `CAST` gives (`1`/`0`, upper case), so the translator rewrites them;
* casts to `varchar(n)` and `fixedchar(n)` are rejected, because SQL Server truncates longer
  values silently;
* casts from floating-point and decimal values to integers truncate, and reducing a timestamp's
  precision rounds. Substrait doesn't say what a cast should do here.

*Suggestion:* an optional cast matrix, e.g. `casts: [{from: [i32, i64], to: [fp64],
failure_options: [...]}]`, and rounding behaviors for numeric and temporal narrowing.

### 9. Variadic functions with zero arguments

`functions_boolean.yaml` declares `and`/`or` with `variadic: {min: 0}`, but the dialect schema
requires `variadic.min >= 1`. Both dialects write `variadic: {}`.

### 10. Limits

SQL Server nests `CASE` at most 10 levels deep, so a plan with deeply nested `if_then`
expressions fails on the server. A very long `IN` list can exhaust the query processor
(error 8623). Identifiers have at most 128 characters, and a function at most 254 arguments.
Only the last is expressible (`variadic.max`).

### 11. Named tables

`read_types: [NAMED_TABLE]` doesn't say how many name parts a table name has or what they mean.
For SQL Server that is `[table]`, `[schema, table]`, `[database, schema, table]` or
`[server, database, schema, table]`. A name starting with `#` is a temporary table. Whether
names are case-sensitive depends on the database's collation.

### 12. Versioning

The top level allows only `name`, `metadata`, `dependencies` and the `supported_*` lists.
Nothing records which Substrait release the dialect was written against. Both dialects put
`substrait_version` in `metadata`.

---

## Gaps around the format

These aren't problems with the schema itself, but they decide whether a dialect helps in
practice.

### Holes in the standard extensions

DataFusion's `max(cname)` on a string column can't be written with the standard extensions.
`min` and `max` are defined in `functions_arithmetic` (numbers) and `functions_datetime`
(temporal types) only, so there is no standard `min` or `max` for strings, booleans, binary or
uuid. Every SQL engine has them. DataFusion emits `max:str` without an extension URN. The SQL
Server driver rejects it, because no standard function matches.

### Producers that omit extension URNs

DataFusion 55 declares functions by bare name with no extension URN. Since a dialect identifies
functions by `(source URN, name, signature)`, such plans can't be matched against it exactly.
Both translators fall back to matching by name and inferred argument types. That works, but is
a guess.

### Non-standard functions and signatures

Of 66 SQL queries run through DataFusion's producer, 10 were rejected. Seven used functions or
signatures that are not in the standard extensions, with no extension declared for them. The
others were a window function, a cast from float to string (gap 8), and a string `max`, which
the standard extensions don't define (above).

| SQL | Emitted | Standard equivalent |
|-----|---------|---------------------|
| `extract(year FROM ts)` | `date_part` | `extract` (functions_datetime) |
| `stddev(x)` | `stddev` | `std_dev` (functions_arithmetic) |
| `length(s)` | `character_length` | `char_length` (functions_string) |
| `substr(s, 2, 3)` | `substring` with i64 positions | `substring:str_i32_i32` |
| `s LIKE 'a%'`, `starts_with(s, 'a')` | `like` with a third argument (the escape character) | `like:str_str` |
| `trim(s)` | `btrim` with one argument | `trim:str_str` |

This is the same list as for the Kusto driver, plus `btrim`. A dialect can't fix this; it only
helps if the producer consults it and limits itself to what it lists.

### Producers that disagree with the specification

With more than one grouping set, an aggregate's output has a final `i32` column: the index of
the grouping set each row came from. The specification puts that column *after* the measures.
DataFusion's plans put it *before* them (as DataFusion does internally), and project the measure
from the wrong ordinal. Following the specification, the translator returns the grouping set
index where DataFusion expects the count. Three of the four differences in the differential test
are this (`ROLLUP`, `CUBE`, `GROUPING SETS`).

DataFusion also plans `INTERSECT ALL` and `EXCEPT ALL` as semi and anti joins, so the duplicate
counts that make them ALL are lost. Over the test data, SQL's multiset semantics give 4 and 5
rows; DataFusion gives 8 and 1, both when it runs the SQL itself and in its plans. The translation
of those plans matches DataFusion, so the differential test doesn't flag it. A consumer can't
detect this kind of error.

A conformance suite would catch both: a set of plans with expected results that producers and
consumers check themselves against. So would a reference validator that knows output schemas.

### Under-specified standard functions

Writing exact translations exposed functions whose meaning a consumer has to guess:

* **`like`** doesn't define its wildcard syntax or an escape character. T-SQL's `LIKE` also
  treats `[...]` as a character class, which the translator escapes, assuming SQL's `%` and `_`
  are the only wildcards. A backslash is an ordinary character. (The Kusto translator rejects
  patterns with a backslash instead: the two consumers read the same gap differently.)
* **Regular expressions** follow ICU, but SQL Server's are RE2. Most syntax is shared. The
  translator rejects patterns using ICU-only constructs it recognizes (lookaround,
  backreferences, atomic groups, possessive quantifiers), and rewrites ICU's `$1` group
  references in replacements to RE2's `\1`. A dialect has no way to name its regex flavor.
* **`extract` MONDAY_WEEK and SUNDAY_WEEK**: "First week starts on first Monday of January",
  with values 1 to 53 (or 0 to 52). Days of January before the first Monday then belong to no
  week. The translator leaves these out rather than guess.
* **`atan2(x, y)`** is "arctangent of values given as x/y pairs", which doesn't say which
  argument is the numerator. Both translators leave it out.
* **`strpos(s, '')`**, **`concat` of only nulls with IGNORE_NULLS**, and **`substring` with a
  negative length** aren't defined. The translator returns 1, `''` and an error (SQL Server's
  behavior), which match PostgreSQL and DataFusion as far as we checked.

Specifications that settle such cases help. `avg` says integer averages truncate, and `product`
says it returns 1 for no rows. Both took one line, and both decided a translation.

### Discovery

The format describes a dialect, but there is no standard way for a consumer to *publish* one,
or for a producer to request it. ADBC has no API for this. This driver returns its dialect from
`GetOption("adbc.mssql.substrait.dialect")` on a database or connection (the only route through
ADBC's C API) and from a static method. The Kusto driver has only the static method. Gap 2
argues that the answer should come from a connection.

---

## What works well

For balance: the dialect format covers much of what a SQL consumer needs, and the SQL Server
dialect uses it as intended.

* Relation-level support, including all twelve `join_types`, the set `operations` and
  `read_types`. For SQL Server every set operation except MINUS_MULTISET can be translated,
  the ALL ones by numbering duplicate rows.
* Expression-level support, including `subquery_types`, cast `failure_options` and
  execution-context variables.
* Types with `max_precision`, `max_scale` and `max_length`.
* Functions by extension and exact signature, with option behaviors. SQL Server raises errors
  where other engines return null or wrap, and options such as `overflow: ERROR` and
  `on_division_by_zero: ERROR` say exactly that.
* `system_metadata.name`/`notation`, which map naturally to an operator-or-function code
  generator.
* Execution behavior (`PER_PLAN`): SQL Server evaluates `SYSUTCDATETIME()` once per query.

The dialect file validates with an off-the-shelf JSON Schema validator.
