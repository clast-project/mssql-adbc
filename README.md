# Clast.Drivers.SqlServer

A high-performance [ADBC](https://arrow.apache.org/adbc/) 1.1 driver for
Microsoft SQL Server, written in C# for .NET 10. It speaks TDS directly and
decodes rows straight into Apache Arrow buffers. See [docs/plan.md](docs/plan.md).

Part of the clast-project. Status: early prototype.

## Building

The driver builds against the ADBC C# sources in the `external/arrow-adbc`
submodule (a shallow clone of [apache/arrow-adbc](https://github.com/apache/arrow-adbc)),
until an ADBC release has the APIs it uses:

```
git clone --recurse-submodules https://github.com/clast-project/mssql-adbc.git
cd mssql-adbc
dotnet build -c Release
```

In an existing clone, run `git submodule update --init` first. To build
against another ADBC checkout, pass `-p:AdbcCSharpRoot=<path>\csharp`.

## Platforms

Windows, Linux and macOS (x64 and ARM64), on .NET 10. CI runs the full test
suite on Linux against SQL Server 2025 in a container, and the tests that need
no server on all three systems.

- Named pipes and LocalDB are Windows-only; elsewhere connect over TCP.
- Integrated authentication uses SSPI on Windows and Kerberos (GSSAPI)
  elsewhere: get a ticket with `kinit` first. NTLM off Windows needs the
  `gss-ntlmssp` package.
- TLS uses SChannel on Windows, OpenSSL on Linux and Apple's TLS stack on
  macOS; the latter may cap strict (TDS 8) connections at TLS 1.2.

## Testing

Tests that need a server read a JSON file named by `MSSQL_TEST_CONFIG_FILE`;
see `test/Clast.Drivers.SqlServer.Tests/Resources/mssqlconfig.json`. The server
must have the TCP protocol enabled.

`WarehouseTests` run against a Fabric Warehouse when `MSSQL_WAREHOUSE_CONFIG_FILE`
names a JSON file with `options` (`adbc.mssql.server`, `adbc.mssql.database`) and
an `accessTokenFile`: the path of a file holding an Entra access token for
`https://database.windows.net/`. They create and drop tables named `clast_wh_*`.

## Benchmarks

```
dotnet run -c Release --project benchmarks/Clast.Drivers.SqlServer.Benchmarks
```

| Variable | Meaning |
|---|---|
| `MSSQL_BENCH_CONNECTION` | SqlClient connection string (default: integrated auth to `tcp:localhost`, `tempdb`) |
| `MSSQL_BENCH_GO_DRIVER` | Path to the Go MSSQL ADBC driver library; enables the Go case |
| `MSSQL_BENCH_GO_URI` | `uri` option for the Go driver |
| `MSSQL_BENCH_CLAST` | `1` to include this driver |
| `MSSQL_BENCH_SERVER`, `MSSQL_BENCH_DATABASE` | Server and database for all drivers (default `localhost`, `tempdb`) |
| `MSSQL_BENCH_ACCESS_TOKEN_FILE` | File holding an Entra access token, for all three drivers to log in with instead of integrated authentication (Fabric, Azure SQL) |

`-- --analyze client [iterations] [read|ingest]` reports wall time and client CPU
per driver, interleaved, and needs nothing from the server; it suits remote
services. On a Fabric Warehouse the benchmark tables are named `clast_bench_*`.

## License

Apache-2.0. Portions are derived from
[Microsoft.Data.SqlClient](https://github.com/dotnet/SqlClient) under the MIT
license; see [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
