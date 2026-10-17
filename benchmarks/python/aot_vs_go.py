# Copyright (c) clast-project. All rights reserved.
# Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.
"""
Benchmarks the NativeAOT driver (adbc_driver_sqlserver) against the Go MSSQL
ADBC driver, both loaded by Python's adbc_driver_manager: the same host, the
same C data interface, nothing that favours either. Reads and ingest of the
1M-row benchmark table (created by the .NET benchmarks, dbo.bench_types_1000000
in tempdb), interleaved, with the server session placed on one CPU and the
client on a CPU set (docs/performance.md), plus a cold start in new processes.
Windows, integrated authentication, a local server; needs adbc-driver-manager,
pyarrow and tzdata.

Usage: aot_vs_go.py <aot-library> <go-library> [iterations] [server-cpu] [client-mask-hex]
       aot_vs_go.py --startup <library> <aot|go>    (one cold load, connect and query)
"""
import ctypes
import statistics
import subprocess
import sys
import time

import adbc_driver_manager.dbapi as dbapi
import pyarrow as pa

TABLE = "dbo.bench_types_1000000"
TARGET = "dbo.bench_ingest_target"
COLUMNS = """
    id bigint NOT NULL, c_tinyint tinyint NULL, c_smallint smallint NULL, c_int int NULL, c_bigint bigint NULL,
    c_bit bit NULL, c_real real NULL, c_float float NULL, c_decimal decimal(18,4) NULL, c_decimal38 decimal(38,10) NULL,
    c_varchar varchar(50) NULL, c_nvarchar nvarchar(50) NULL, c_varbinary varbinary(16) NULL, c_date date NULL,
    c_datetime2 datetime2(7) NULL, c_datetimeoffset datetimeoffset(7) NULL, c_uniqueidentifier uniqueidentifier NULL
"""
CPU_QUERY = ("SELECT s.cpu_id FROM sys.dm_exec_requests r JOIN sys.dm_os_schedulers s "
             "ON s.scheduler_id = r.scheduler_id WHERE r.session_id = @@SPID")


def connect(kind, driver):
    if kind == "aot":
        return dbapi.connect(driver=driver, db_kwargs={
            "adbc.mssql.server": "localhost", "adbc.mssql.database": "tempdb",
            "adbc.mssql.integrated_security": "true", "adbc.mssql.trust_server_certificate": "true"})
    # Full TLS, as the Clast default (mandatory encryption).
    return dbapi.connect(driver=driver, db_kwargs={
        "uri": "sqlserver://localhost?database=tempdb&trusted_connection=true&TrustServerCertificate=true&encrypt=true"})


def server_cpu(conn):
    with conn.cursor() as cur:
        cur.execute(CPU_QUERY)
        return int(cur.fetchone()[0])


def connect_placed(kind, driver, cpu):
    """Reconnects until the session's requests run on server CPU `cpu`; the rejects stay open meanwhile."""
    rejected = []
    try:
        for _ in range(300):
            conn = connect(kind, driver)
            if cpu is None or server_cpu(conn) == cpu:
                return conn
            rejected.append(conn)
        raise RuntimeError(f"No {kind} connection landed on server CPU {cpu}")
    finally:
        for r in rejected:
            r.close()


def read(conn):
    with conn.cursor() as cur:
        cur.execute(f"SELECT * FROM {TABLE}")
        return cur.fetch_arrow_table()


def ingest(conn, data):
    with conn.cursor() as cur:
        cur.execute(f"TRUNCATE TABLE {TARGET}")
        cur.adbc_ingest("bench_ingest_target", data, mode="append", db_schema_name="dbo")
    conn.commit()


def measure(fn):
    cpu0, wall0 = time.process_time(), time.perf_counter()
    result = fn()
    return (time.perf_counter() - wall0) * 1000, (time.process_time() - cpu0) * 1000, result


def stats(samples):
    s = sorted(samples)
    return f"{statistics.median(s):7.0f} ({s[0]:.0f}-{s[-1]:.0f})"


def startup(driver, kind):
    """In a fresh process: load the driver, connect and run one query."""
    wall0, cpu0 = time.perf_counter(), time.process_time()
    with connect(kind, driver) as conn, conn.cursor() as cur:
        cur.execute("SELECT 1")
        cur.fetchall()
    print(f"{(time.perf_counter() - wall0) * 1000:.1f} {(time.process_time() - cpu0) * 1000:.1f}")


def main():
    if sys.argv[1] == "--startup":
        startup(sys.argv[2], sys.argv[3])
        return
    aot, go = sys.argv[1], sys.argv[2]
    iterations = int(sys.argv[3]) if len(sys.argv) > 3 else 8
    cpu = int(sys.argv[4]) if len(sys.argv) > 4 else 16
    mask = int(sys.argv[5], 16) if len(sys.argv) > 5 else 0xF
    drivers = {"aot": aot, "go": go}

    # Cold start first, in fresh processes (no placement, no affinity: a first query as a script sees it).
    print("Startup: load + connect + SELECT 1 in a new process (ms; medians of 5)")
    for kind, path in drivers.items():
        walls, cpus = [], []
        for _ in range(5):
            out = subprocess.run([sys.executable, "-I", __file__, "--startup", path, kind],
                                 capture_output=True, text=True, check=True).stdout.split()
            walls.append(float(out[0]))
            cpus.append(float(out[1]))
        print(f"  {kind:4} wall {stats(walls)}  cpu {stats(cpus)}")

    ctypes.windll.kernel32.SetProcessAffinityMask(ctypes.windll.kernel32.GetCurrentProcess(), mask)
    conns = {kind: connect_placed(kind, path, cpu) for kind, path in drivers.items()}

    # The ingest source: the table as Clast reads it, tinyint widened (the Go driver
    # cannot ingest uint8), the same Arrow data for both.
    clast_schema = read(conns["aot"]).schema
    source = read(conns["aot"])
    source = source.set_column(1, "c_tinyint", source.column(1).cast(pa.int16()))
    with conns["aot"].cursor() as cur:
        cur.execute(f"DROP TABLE IF EXISTS {TARGET}; CREATE TABLE {TARGET} ({COLUMNS})")
    conns["aot"].commit()

    results = {(w, k): ([], []) for w in ("read", "ingest") for k in drivers}
    for kind, conn in conns.items():  # warm up
        read(conn)
        ingest(conn, source)
    for i in range(iterations):
        for workload in ("read", "ingest"):
            for kind, conn in conns.items():
                wall, cpu_ms, result = measure(lambda: read(conn) if workload == "read" else ingest(conn, source))
                if workload == "read":
                    assert result.num_rows == 1_000_000, result.num_rows
                results[(workload, kind)][0].append(wall)
                results[(workload, kind)][1].append(cpu_ms)
        print(".", end="", file=sys.stderr, flush=True)
    print(file=sys.stderr)

    print(f"\n1M rows x 17 columns, full TLS, server CPU {cpu}, client mask 0x{mask:X}; {iterations} interleaved runs (median, min-max ms)")
    print(f"  {'':12} {'wall':>22} {'client cpu':>22}")
    for (workload, kind), (walls, cpus) in results.items():
        print(f"  {workload:6} {kind:5} {stats(walls):>22} {stats(cpus):>22}")
    print("\nRead schemas differ:")
    go_schema = read(conns["go"]).schema
    for field in clast_schema:
        other = go_schema.field(field.name).type if field.name in go_schema.names else None
        if str(other) != str(field.type):
            print(f"  {field.name}: clast {field.type} | go {other}")
    for conn in conns.values():
        conn.close()


if __name__ == "__main__":
    main()
