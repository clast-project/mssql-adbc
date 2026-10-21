// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Data;
using System.Diagnostics;
using System.Globalization;
using Apache.Arrow;
using Apache.Arrow.Adbc;
using Apache.Arrow.Ipc;
using Clast.Drivers.SqlServer.Tds;
using Microsoft.Data.SqlClient;

namespace Clast.Drivers.SqlServer.Benchmarks;

/// <summary>
/// Explains where time goes rather than just timing it. For each case it
/// records wall time and client CPU time, and from the server's DMVs the
/// session's CPU time, its waits and the packets sent. Cases are interleaved
/// so drift in the server or machine affects them all alike.
/// </summary>
/// <remarks>
/// Run with <c>dotnet run -c Release --project benchmarks/Clast.Drivers.SqlServer.Benchmarks -- --analyze read|ingest [iterations]</c>.
/// </remarks>
internal static class Analysis
{
    private sealed class Case(string name, Func<int> sessionId, Action run, Action? before = null)
    {
        public string Name { get; } = name;
        public Func<int> SessionId { get; } = sessionId;
        public Action Run { get; } = run;
        public Action? Before { get; } = before;
        public List<Sample> Samples { get; } = new();
    }

    /// <param name="ServerCpu">The logical CPU the server ran the request on most often (-1 if never seen).</param>
    /// <param name="ClientCpu">The logical CPU the client thread was on when the run started.</param>
    private sealed record Sample(double WallMs, double ClientCpuMs, double ServerCpuMs, double ServerElapsedMs, long Packets, Dictionary<string, double> WaitMs,
        int ServerCpu, int ClientCpu, double ThreadMegacycles);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool QueryThreadCycleTime(IntPtr thread, out ulong cycles);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentThread();

    /// <summary>CPU cycles the calling thread has used (Windows; 0 elsewhere); precise, unlike process CPU time.</summary>
    private static ulong ThreadCycles() =>
        OperatingSystem.IsWindows() && QueryThreadCycleTime(GetCurrentThread(), out ulong cycles) ? cycles : 0;

    /// <summary>On this machine (i9-12900K) CPUs 0-15 are P-core threads (siblings 2k, 2k+1) and 16-23 are E-cores.</summary>
    private static bool IsEfficiencyCore(int cpu) => cpu >= EfficiencyCoreStart;

    private static readonly int EfficiencyCoreStart =
        int.TryParse(Environment.GetEnvironmentVariable("MSSQL_BENCH_ECORE_START"), out int start) ? start : 16;

    private const int Rows = 1_000_000;

    public static void Run(string[] args)
    {
        string mode = args.Length > 1 ? args[1] : "read";
        int iterations = args.Length > 2 ? int.Parse(args[2], CultureInfo.InvariantCulture) : 8;
        // The DMV-based modes need SQL Server's sys.dm_exec_* and sys.dm_os_schedulers views.
        if (mode is not ("encode" or "client" or "profile") && BenchmarkData.Warehouse)
        {
            Console.Error.WriteLine(
                $"--analyze {mode} reads SQL Server DMVs that a warehouse does not have; use --analyze client to compare drivers there");
            Environment.ExitCode = 1;
            return;
        }
        BenchmarkData.EnsureTable(Rows);
        // Optional placement control: the server CPU every measured session must run on
        // (MSSQL_BENCH_SERVER_CPU, see ServerPlacement), and the client CPUs.
        if (long.TryParse(Environment.GetEnvironmentVariable("MSSQL_BENCH_CLIENT_AFFINITY"), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long mask) && OperatingSystem.IsWindows())
        {
            Process.GetCurrentProcess().ProcessorAffinity = (IntPtr)mask;
        }
        if (mode == "encode")
        {
            EncodeAnalysis.Run(iterations);
            return;
        }
        if (mode == "client")
        {
            int[] batchSizes = args.Length > 4 ? args[4].Split(',').Select(s => int.Parse(s, CultureInfo.InvariantCulture)).ToArray() : [0];
            ClientCpu.Run(iterations, args.Length > 3 ? args[3] : "read", batchSizes);
            return;
        }
        if (mode == "sessions")
        {
            CompareSessions();
            return;
        }
        if (mode == "profile")
        {
            // A steady loop for a CPU sampler: one driver, one encryption mode.
            string encryption = args.Length > 3 ? args[3] : "login";
            using AdbcConnection connection = new SqlServerDriver().Open(BenchmarkSettings.ClastOptionsFor(encryption)).Connect(null);
            for (int i = 0; i < iterations; i++)
            {
                ReadClast(connection);
            }
            return;
        }

        using var observer = BenchmarkSettings.NewSqlConnection(BenchmarkSettings.SqlClientConnectionString);
        observer.Open();
        var disposables = new List<IDisposable>();
        List<Case> cases = mode switch
        {
            "ingest" => IngestCases(observer, disposables),
            "transport" => TransportCases(disposables),
            _ => ReadCases(disposables),
        };

        // Warm up every case, then interleave.
        foreach (Case c in cases)
        {
            c.Before?.Invoke();
            c.Run();
        }
        for (int i = 0; i < iterations; i++)
        {
            foreach (Case c in cases)
            {
                c.Samples.Add(Measure(observer, c));
            }
            Console.Error.Write('.');
        }
        Console.Error.WriteLine();

        Report(cases);
        foreach (IDisposable d in disposables)
        {
            d.Dispose();
        }
    }

    private static Sample Measure(SqlConnection observer, Case c)
    {
        c.Before?.Invoke();
        int session = c.SessionId();
        var (cpu0, elapsed0) = SessionTimes(observer, session);
        long packets0 = Packets(observer, session);
        Dictionary<string, (long Count, double Ms)> waits0 = Waits(observer, session);

        GC.Collect();
        GC.WaitForPendingFinalizers();

        // Sample which CPU the server runs the request on, from a separate connection.
        var serverCpus = new Dictionary<int, int>();
        using var stop = new CancellationTokenSource();
        Task sampler = Task.Factory.StartNew(() =>
        {
            using var probe = BenchmarkSettings.NewSqlConnection(BenchmarkSettings.SqlClientConnectionString);
            probe.Open();
            using var command = new SqlCommand(
                "SELECT s.cpu_id FROM sys.dm_exec_requests r JOIN sys.dm_os_schedulers s ON s.scheduler_id = r.scheduler_id WHERE r.session_id = @s", probe);
            command.Parameters.AddWithValue("@s", session);
            while (!stop.IsCancellationRequested)
            {
                if (command.ExecuteScalar() is int cpu)
                {
                    serverCpus[cpu] = serverCpus.GetValueOrDefault(cpu) + 1;
                }
                Thread.Sleep(5);
            }
        }, TaskCreationOptions.LongRunning);
        Thread.Sleep(50);

        int clientCpu = Thread.GetCurrentProcessorId();
        TimeSpan clientCpu0 = Process.GetCurrentProcess().TotalProcessorTime;
        ulong cycles0 = ThreadCycles();
        var watch = Stopwatch.StartNew();
        c.Run();
        watch.Stop();
        ulong cycles1 = ThreadCycles();
        stop.Cancel();
        sampler.Wait();
        TimeSpan clientCpu1 = Process.GetCurrentProcess().TotalProcessorTime;

        var (cpu1, elapsed1) = SessionTimes(observer, session);
        long packets1 = Packets(observer, session);
        Dictionary<string, (long Count, double Ms)> waits1 = Waits(observer, session);
        var waitDelta = new Dictionary<string, double>();
        foreach (var (type, value) in waits1)
        {
            double before = waits0.TryGetValue(type, out var w) ? w.Ms : 0;
            if (value.Ms - before > 0)
            {
                waitDelta[type] = value.Ms - before;
            }
        }

        return new Sample(watch.Elapsed.TotalMilliseconds, (clientCpu1 - clientCpu0).TotalMilliseconds,
            cpu1 - cpu0, elapsed1 - elapsed0, packets1 - packets0, waitDelta,
            serverCpus.Count > 0 ? serverCpus.MaxBy(p => p.Value).Key : -1, clientCpu, (cycles1 - cycles0) / 1e6);
    }

    /// <summary>The session's CPU and elapsed time over its completed requests, in ms.</summary>
    private static (double Cpu, double Elapsed) SessionTimes(SqlConnection observer, int session)
    {
        using var command = new SqlCommand("SELECT cpu_time, total_elapsed_time FROM sys.dm_exec_sessions WHERE session_id = @s", observer);
        command.Parameters.AddWithValue("@s", session);
        using SqlDataReader reader = command.ExecuteReader();
        reader.Read();
        return (reader.GetInt32(0), reader.GetInt32(1));
    }

    private static long Packets(SqlConnection observer, int session)
    {
        using var command = new SqlCommand("SELECT CAST(num_writes AS bigint) FROM sys.dm_exec_connections WHERE session_id = @s", observer);
        command.Parameters.AddWithValue("@s", session);
        return (long)command.ExecuteScalar();
    }

    private static Dictionary<string, (long, double)> Waits(SqlConnection observer, int session)
    {
        var result = new Dictionary<string, (long, double)>();
        using var command = new SqlCommand("SELECT wait_type, waiting_tasks_count, wait_time_ms FROM sys.dm_exec_session_wait_stats WHERE session_id = @s", observer);
        command.Parameters.AddWithValue("@s", session);
        using SqlDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            result[reader.GetString(0)] = (reader.GetInt64(1), reader.GetInt64(2));
        }
        return result;
    }

    private static void Report(List<Case> cases)
    {
        static double Median(IEnumerable<double> values)
        {
            double[] sorted = values.OrderBy(v => v).ToArray();
            return sorted.Length % 2 == 1 ? sorted[sorted.Length / 2] : (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2;
        }

        Console.WriteLine("Medians over interleaved iterations (ms unless noted)");
        Console.WriteLine($"{"case",-34} {"wall",8} {"min",8} {"cliCPU",8} {"srvCPU",8} {"srvElap",8} {"packets",8}  top server waits");
        foreach (Case c in cases)
        {
            var waitTypes = c.Samples.SelectMany(s => s.WaitMs.Keys).Distinct();
            var waits = waitTypes
                .Select(t => (Type: t, Ms: Median(c.Samples.Select(s => s.WaitMs.GetValueOrDefault(t)))))
                .Where(w => w.Ms >= 1)
                .OrderByDescending(w => w.Ms)
                .Take(4)
                .Select(w => $"{w.Type}={w.Ms:F0}");
            Console.WriteLine(
                $"{c.Name,-34} {Median(c.Samples.Select(s => s.WallMs)),8:F0} {c.Samples.Min(s => s.WallMs),8:F0} " +
                $"{Median(c.Samples.Select(s => s.ClientCpuMs)),8:F0} {Median(c.Samples.Select(s => s.ServerCpuMs)),8:F0} " +
                $"{Median(c.Samples.Select(s => s.ServerElapsedMs)),8:F0} {Median(c.Samples.Select(s => (double)s.Packets)),8:F0}  {string.Join(" ", waits)}");
        }

        if (OperatingSystem.IsWindows())
        {
            Console.WriteLine();
            Console.WriteLine("Reading thread CPU (QueryThreadCycleTime), millions of cycles");
            Console.WriteLine($"{"case",-34} {"mean",8} {"median",8} {"min",8} {"stddev",8}");
            foreach (Case c in cases)
            {
                double[] cycles = c.Samples.Select(x => x.ThreadMegacycles).ToArray();
                double mean = cycles.Average();
                double stddev = Math.Sqrt(cycles.Select(x => (x - mean) * (x - mean)).Average());
                Console.WriteLine($"{c.Name,-34} {mean,8:F1} {Median(cycles),8:F1} {cycles.Min(),8:F1} {stddev,8:F1}");
            }
        }

        Console.WriteLine();
        Console.WriteLine("Wall time of every sample, sorted");
        foreach (Case c in cases)
        {
            Console.WriteLine($"{c.Name,-34} {string.Join(" ", c.Samples.Select(x => x.WallMs).Order().Select(w => w.ToString("F0", CultureInfo.InvariantCulture)))}");
        }

        Console.WriteLine();
        Console.WriteLine("Split by the core the server ran the request on: median wall (count) / median server CPU");
        Console.WriteLine($"{"case",-34} {"P-core",18} {"E-core",18}   server CPUs seen");
        foreach (Case c in cases)
        {
            string Split(bool efficiency)
            {
                Sample[] group = c.Samples.Where(s => s.ServerCpu >= 0 && IsEfficiencyCore(s.ServerCpu) == efficiency).ToArray();
                return group.Length == 0 ? "-" : $"{Median(group.Select(s => s.WallMs)):F0} ({group.Length}) / {Median(group.Select(s => s.ServerCpuMs)):F0}";
            }
            Console.WriteLine($"{c.Name,-34} {Split(false),18} {Split(true),18}   {string.Join(",", c.Samples.Select(s => $"{s.ServerCpu}/{s.ClientCpu}"))}");
        }
    }

    /// <summary>Prints the session and connection settings the server holds for each driver's session.</summary>
    private static void CompareSessions()
    {
        using AdbcConnection clast = new SqlServerDriver().Open(BenchmarkSettings.ClastOptionsFor("full")).Connect(null);
        using var sql = BenchmarkSettings.NewSqlConnection(BenchmarkSettings.SqlClientConnectionStringFor("full"));
        sql.Open();
        int clastSession = AdbcSessionId(clast);

        using var observer = BenchmarkSettings.NewSqlConnection(BenchmarkSettings.SqlClientConnectionString);
        observer.Open();
        using var command = new SqlCommand("""
            SELECT s.*, c.*, r.scheduler_id FROM sys.dm_exec_sessions s
            JOIN sys.dm_exec_connections c ON c.session_id = s.session_id
            LEFT JOIN sys.dm_exec_requests r ON r.session_id = s.session_id
            WHERE s.session_id IN (@a, @b) ORDER BY s.session_id
            """, observer);
        command.Parameters.AddWithValue("@a", clastSession);
        command.Parameters.AddWithValue("@b", sql.ServerProcessId);
        using SqlDataReader reader = command.ExecuteReader();
        var rows = new List<object[]>();
        while (reader.Read())
        {
            var values = new object[reader.FieldCount];
            reader.GetValues(values);
            rows.Add(values);
        }
        Console.WriteLine($"Clast session {clastSession}, SqlClient session {sql.ServerProcessId}; columns that differ:");
        for (int i = 0; i < reader.FieldCount; i++)
        {
            string a = Convert.ToString(rows[0][i], CultureInfo.InvariantCulture) ?? "", b = Convert.ToString(rows[1][i], CultureInfo.InvariantCulture) ?? "";
            if (a != b && rows[0][i] is not byte[])
            {
                Console.WriteLine($"  {reader.GetName(i),-32} {a,-40} {b}");
            }
        }
    }

    private static AdbcConnection OpenClast(IReadOnlyDictionary<string, string> options) =>
        ServerPlacement.OpenAdbc(new SqlServerDriver().Open(options));

    private static SqlConnection OpenSqlClient(string connectionString) => ServerPlacement.OpenSqlClient(connectionString);

    /// <summary>The same reads over different addresses for the same server.</summary>
    private static List<Case> TransportCases(List<IDisposable> disposables)
    {
        var cases = new List<Case>();
        string lan = System.Net.Dns.GetHostAddresses(System.Net.Dns.GetHostName())
            .First(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork).ToString();
        foreach (string server in (string[])["localhost"])
        {
            var options = new Dictionary<string, string>(BenchmarkSettings.ClastOptionsFor("full")) { [SqlServerParameters.Server] = server };
            AdbcConnection clast = OpenClast(options);
            disposables.Add(clast);
            int session = AdbcSessionId(clast);
            cases.Add(new Case($"Clast decode {server}", () => session, () => ReadClast(clast)));
        }
        foreach (string server in (string[])["tcp:localhost", "tcp:127.0.0.1", "tcp:[::1]", "tcp:" + lan])
        {
            SqlConnection sql;
            try
            {
                sql = OpenSqlClient($"Server={server};Database=tempdb;Integrated Security=true;TrustServerCertificate=true;Encrypt=true;Packet Size=16384");
            }
            catch (SqlException ex)
            {
                Console.WriteLine($"skipping {server}: {ex.Message}");
                continue;
            }
            disposables.Add(sql);
            int session = sql.ServerProcessId;
            cases.Add(new Case($"SqlClient skip {server} pkt=16384", () => session, () => ReadSqlClient(sql, typed: false)));
        }
        return cases;
    }

    // ---- Reads ----

    private static string Query => $"SELECT * FROM {BenchmarkData.TableName(Rows)}";

    private static List<Case> ReadCases(List<IDisposable> disposables)
    {
        var cases = new List<Case>();
        foreach (string encryption in (string[])["full", "login"])
        {
            foreach (int? packetSize in (int?[])[null, 8000])
            {
                var options = new Dictionary<string, string>(BenchmarkSettings.ClastOptionsFor(encryption));
                if (packetSize != null)
                {
                    options[SqlServerParameters.PacketSize] = packetSize.Value.ToString(CultureInfo.InvariantCulture);
                }
                AdbcConnection clast = OpenClast(options);
                disposables.Add(clast);
                int clastSession = AdbcSessionId(clast);
                string suffix = $"{encryption}{(packetSize != null ? " pkt=" + packetSize : "")}";
                cases.Add(new Case($"Clast decode {suffix}", () => clastSession, () => ReadClast(clast)));
                cases.Add(new Case($"Clast drain {suffix}", () => clastSession, () => DrainClast(clast)));
            }

            foreach (int? packetSize in (int?[])[null, 16384])
            {
                string connectionString = BenchmarkSettings.SqlClientConnectionStringFor(encryption) + (packetSize != null ? $";Packet Size={packetSize}" : "");
                SqlConnection sql = OpenSqlClient(connectionString);
                disposables.Add(sql);
                int sqlSession = sql.ServerProcessId;
                string suffix = $"{encryption}{(packetSize != null ? " pkt=" + packetSize : "")}";
                cases.Add(new Case($"SqlClient typed {suffix}", () => sqlSession, () => ReadSqlClient(sql, typed: true)));
                cases.Add(new Case($"SqlClient skip {suffix}", () => sqlSession, () => ReadSqlClient(sql, typed: false)));
            }
        }
        return cases;
    }

    private static int AdbcSessionId(AdbcConnection connection)
    {
        using AdbcStatement statement = connection.CreateStatement();
        statement.SqlQuery = "SELECT CAST(@@SPID AS int)";
        using IArrowArrayStream stream = statement.ExecuteQuery().Stream!;
        using RecordBatch batch = stream.ReadNextRecordBatchAsync().AsTask().GetAwaiter().GetResult()!;
        int id = ((Int32Array)batch.Column(0)).GetValue(0)!.Value;
        while (stream.ReadNextRecordBatchAsync().AsTask().GetAwaiter().GetResult() is RecordBatch extra)
        {
            extra.Dispose();
        }
        return id;
    }

    private static void ReadClast(AdbcConnection connection)
    {
        using AdbcStatement statement = connection.CreateStatement();
        statement.SqlQuery = Query;
        using IArrowArrayStream stream = statement.ExecuteQuery().Stream!;
        while (stream.ReadNextRecordBatchAsync().AsTask().GetAwaiter().GetResult() is RecordBatch batch)
        {
            batch.Dispose();
        }
    }

    /// <summary>Reads the response without decoding values: transport, TLS and token framing only.</summary>
    private static void DrainClast(AdbcConnection connection)
    {
        TdsSession session = ((SqlServerConnection)connection).Session;
        session.SendSqlBatch(Query);
        TdsColumn[] columns = session.ReadUntilResultSet()!;
        session.DrainResponse(columns);
    }

    private static void ReadSqlClient(SqlConnection connection, bool typed)
    {
        using var command = new SqlCommand(Query, connection) { CommandTimeout = 0 };
        using SqlDataReader reader = command.ExecuteReader(CommandBehavior.SequentialAccess);
        long sink = 0;
        byte[] binary = new byte[16];
        while (reader.Read())
        {
            if (!typed)
            {
                continue;
            }
            sink += reader.GetInt64(0);
            if (!reader.IsDBNull(1)) sink += reader.GetByte(1);
            if (!reader.IsDBNull(2)) sink += reader.GetInt16(2);
            if (!reader.IsDBNull(3)) sink += reader.GetInt32(3);
            if (!reader.IsDBNull(4)) sink += reader.GetInt64(4);
            if (!reader.IsDBNull(5)) sink += reader.GetBoolean(5) ? 1 : 0;
            if (!reader.IsDBNull(6)) sink += (long)reader.GetFloat(6);
            if (!reader.IsDBNull(7)) sink += (long)reader.GetDouble(7);
            if (!reader.IsDBNull(8)) sink += reader.GetSqlDecimal(8).Scale;
            if (!reader.IsDBNull(9)) sink += reader.GetSqlDecimal(9).Scale;
            if (!reader.IsDBNull(10)) sink += reader.GetString(10).Length;
            if (!reader.IsDBNull(11)) sink += reader.GetString(11).Length;
            if (!reader.IsDBNull(12)) sink += reader.GetBytes(12, 0, binary, 0, binary.Length);
            if (!reader.IsDBNull(13)) sink += reader.GetDateTime(13).Ticks;
            if (!reader.IsDBNull(14)) sink += reader.GetDateTime(14).Ticks;
            if (!reader.IsDBNull(15)) sink += reader.GetDateTimeOffset(15).Ticks;
            if (!reader.IsDBNull(16)) sink += reader.GetGuid(16).GetHashCode();
        }
        GC.KeepAlive(sink);
    }

    // ---- Ingest ----

    private static string TargetName => BenchmarkData.Prefix + "ingest_target";

    private static string Target => "dbo." + TargetName;

    private static List<Case> IngestCases(SqlConnection observer, List<IDisposable> disposables)
    {
        using (var create = new SqlCommand($"DROP TABLE IF EXISTS {Target}; CREATE TABLE {Target} ({BenchmarkData.Columns})", observer))
        {
            create.ExecuteNonQuery();
        }
        void Truncate()
        {
            using var truncate = new SqlCommand($"TRUNCATE TABLE {Target}", observer);
            truncate.ExecuteNonQuery();
        }

        var batches = new List<RecordBatch>();
        using (AdbcConnection loader = new SqlServerDriver().Open(BenchmarkSettings.ClastOptions).Connect(null))
        using (AdbcStatement statement = loader.CreateStatement())
        {
            statement.SqlQuery = Query;
            using IArrowArrayStream stream = statement.ExecuteQuery().Stream!;
            while (stream.ReadNextRecordBatchAsync().AsTask().GetAwaiter().GetResult() is RecordBatch batch)
            {
                batches.Add(batch);
            }
        }
        var table = new DataTable();
        using (var adapter = new SqlDataAdapter(Query, observer))
        {
            adapter.Fill(table);
        }

        var cases = new List<Case>();
        foreach (bool tableLock in (bool[])[false, true])
        {
            foreach (int? packetSize in (int?[])[null, 8000])
            {
                var options = new Dictionary<string, string>(BenchmarkSettings.ClastOptions);
                if (packetSize != null)
                {
                    options[SqlServerParameters.PacketSize] = packetSize.Value.ToString(CultureInfo.InvariantCulture);
                }
                AdbcConnection clast = OpenClast(options);
                disposables.Add(clast);
                int session = AdbcSessionId(clast);
                bool tl = tableLock;
                cases.Add(new Case($"Clast tablock={tl}{(packetSize != null ? " pkt=" + packetSize : "")}", () => session, () =>
                {
                    using AdbcStatement statement = clast.CreateStatement();
                    statement.SetOption(AdbcOptions.Ingest.TargetTable, TargetName);
                    statement.SetOption(AdbcOptions.Ingest.TargetDbSchema, "dbo");
                    statement.SetOption(AdbcOptions.Ingest.Mode, AdbcOptions.IngestMode.Append);
                    statement.SetOption(SqlServerParameters.IngestTableLock, tl ? "true" : "false");
                    statement.BindStream(new SharedBatches(batches));
                    statement.ExecuteUpdate();
                }, Truncate));
            }

            foreach (int? packetSize in (int?[])[null, 16384])
            {
                SqlConnection sql = OpenSqlClient(BenchmarkSettings.SqlClientConnectionString + (packetSize != null ? $";Packet Size={packetSize}" : ""));
                disposables.Add(sql);
                int session = sql.ServerProcessId;
                bool tl = tableLock;
                cases.Add(new Case($"SqlBulkCopy tablock={tl}{(packetSize != null ? " pkt=" + packetSize : "")}", () => session, () =>
                {
                    using var bulkCopy = new SqlBulkCopy(sql, tl ? SqlBulkCopyOptions.TableLock : SqlBulkCopyOptions.Default, null)
                    {
                        DestinationTableName = Target,
                        BulkCopyTimeout = 0,
                    };
                    bulkCopy.WriteToServer(table);
                }, Truncate));
            }
        }
        disposables.Add(table);
        return cases;
    }

    private sealed class SharedBatches(List<RecordBatch> batches) : IArrowArrayStream
    {
        private int _next;

        public Schema Schema => batches[0].Schema;

        public ValueTask<RecordBatch?> ReadNextRecordBatchAsync(CancellationToken cancellationToken = default) =>
            new(_next < batches.Count ? batches[_next].SliceShared(0, batches[_next++].Length) : null);

        public void Dispose()
        {
        }
    }
}
