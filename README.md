# Clast.Drivers.SqlServer

A high-performance [ADBC](https://arrow.apache.org/adbc/) 1.1 driver for
Microsoft SQL Server, written in C# for .NET 10. It speaks TDS directly and
decodes rows straight into Apache Arrow buffers. See [docs/plan.md](docs/plan.md).

It also executes [Substrait](https://substrait.io/) plans, translated to T-SQL,
and publishes a Substrait dialect describing what translates; see
[docs/substrait.md](docs/substrait.md), and
[docs/substrait-dialect-gaps.md](docs/substrait-dialect-gaps.md) for what the
dialect format can't express.

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

A netstandard2.0 build serves other hosts, chiefly .NET Framework 4.7.2 and
later. On Windows the test project also targets net472, so the tests run
against that build as well. Compared with the .NET 10 build:

- Integrated authentication needs Windows (it calls SSPI directly).
- Strict (TDS 8) connections offer no ALPN protocol, and get TLS 1.3 only
  where the runtime and Windows support it (.NET Framework 4.8 on Windows 11
  or Server 2022); other TLS versions are the system defaults.
- Under TLS the packet size is at most 16384 bytes: .NET Framework splits
  larger packets into TLS records that SQL Server refuses.
- It is written for correctness rather than speed: newer .NET's span-based
  APIs are filled in over arrays, and 128-bit decimals use a portable integer.

## NativeAOT driver

`src/Clast.Drivers.SqlServer.Native` compiles the driver with NativeAOT into a
native shared library, `adbc_driver_sqlserver` (`.dll`, `.so` or `.dylib`).
It exports `AdbcDriverInit` and needs no .NET runtime, so any ADBC driver
manager can load it: C/C++, Python's `adbc_driver_manager`, Go, Rust or R.
The layout follows the adbc-drivers HiveServer2 driver.

```
dotnet publish src/Clast.Drivers.SqlServer.Native -c Release -r win-x64 -o artifacts/aot-fixture
```

NativeAOT doesn't cross-compile, so publish on the target platform. On
Windows it links with MSVC: use a Developer shell, or put `vswhere.exe` (in
`Microsoft Visual Studio\Installer`) on PATH. Elsewhere it needs clang.
`ci/scripts/csharp_aot.sh <workspace> <rid>` publishes and runs
`test/Clast.Drivers.SqlServer.Native.Tests` against the library. Those tests
cover the fake TDS server, and also a real server when
`MSSQL_TEST_CONFIG_FILE` is set. CI builds win-x64, linux-x64, linux-arm64 and
osx-arm64, then `ci/scripts/csharp_aot_pack.sh` packs them into
`Clast.Drivers.SqlServer.Native`, a NuGet package of native libraries under
`runtimes/<rid>/native/`.

`benchmarks/python/aot_vs_go.py` compares it with the Go driver, both loaded by Python's driver manager (see docs/performance.md). For example, from Python:

```python
import adbc_driver_manager.dbapi as dbapi

with dbapi.connect(driver="adbc_driver_sqlserver.dll", db_kwargs={
        "adbc.mssql.server": "localhost", "adbc.mssql.integrated_security": "true"}) as conn:
    with conn.cursor() as cur:
        cur.execute("SELECT name FROM sys.databases")
        print(cur.fetch_arrow_table())
```

## Testing

Tests that need a server read a JSON file named by `MSSQL_TEST_CONFIG_FILE`;
see `test/Clast.Drivers.SqlServer.Tests/Resources/mssqlconfig.json`. The server
must have the TCP protocol enabled.

`WarehouseTests` run against a Fabric Warehouse when `MSSQL_WAREHOUSE_CONFIG_FILE`
names a JSON file with `options` (`adbc.mssql.server`, `adbc.mssql.database`) and
an `accessTokenFile`: the path of a file holding an Entra access token for
`https://database.windows.net/`. They create and drop tables named `clast_wh_*`.

The Substrait tests use plans DataFusion produced (`Fixtures/DataFusion`);
`scripts/gen_datafusion_fixtures.py` regenerates them, and
`MSSQL_UPDATE_SNAPSHOTS=1` rewrites the reviewed T-SQL snapshots.

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
