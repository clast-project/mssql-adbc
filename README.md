# Clast.Drivers.SqlServer

A high-performance [ADBC](https://arrow.apache.org/adbc/) 1.1 driver for
Microsoft SQL Server, written in C# for .NET 10. It speaks TDS directly and
decodes rows straight into Apache Arrow buffers. See [docs/plan.md](docs/plan.md).

Part of the clast-project. Status: early prototype.

## Building

The driver references the ADBC C# sources from a local checkout, set by
`AdbcCSharpRoot` in `Directory.Build.props`:

```
dotnet build -c Release
```

## Testing

Tests that need a server read a JSON file named by `MSSQL_TEST_CONFIG_FILE`;
see `test/Clast.Drivers.SqlServer.Tests/Resources/mssqlconfig.json`. The server
must have the TCP protocol enabled.

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

## License

Apache-2.0. Portions are derived from
[Microsoft.Data.SqlClient](https://github.com/dotnet/SqlClient) under the MIT
license; see [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
