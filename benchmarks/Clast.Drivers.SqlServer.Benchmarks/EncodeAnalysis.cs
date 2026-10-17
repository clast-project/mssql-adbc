// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Diagnostics;
using System.Runtime.InteropServices;
using Apache.Arrow;
using Apache.Arrow.Adbc;
using Apache.Arrow.Ipc;
using Clast.Drivers.SqlServer.Arrow;
using Clast.Drivers.SqlServer.Tds;

namespace Clast.Drivers.SqlServer.Benchmarks;

/// <summary>
/// Bulk-ingest encoding alone: the benchmark table's batches, encoded by the
/// encoders the driver would use for the ingest target, into a packet writer
/// over Stream.Null. No TLS, socket or server, so this is the share of
/// ingest's client CPU that the encoders account for. Reports reading-thread
/// cycles (Windows) and wall time, for all columns together and each alone.
/// <c>--analyze encode [iterations]</c>.
/// </summary>
internal static class EncodeAnalysis
{
    [DllImport("kernel32.dll")]
    private static extern bool QueryThreadCycleTime(IntPtr thread, out ulong cycles);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentThread();

    private static ulong Cycles() => OperatingSystem.IsWindows() && QueryThreadCycleTime(GetCurrentThread(), out ulong c) ? c : 0;

    public static void Run(int iterations)
    {
        using var driver = new SqlServerDriver();
        using AdbcDatabase database = driver.Open(BenchmarkSettings.ClastOptions);
        using AdbcConnection connection = database.Connect(null);
        TdsSession session = ((SqlServerConnection)connection).Session;

        var batches = new List<RecordBatch>();
        using (AdbcStatement statement = connection.CreateStatement())
        {
            statement.SqlQuery = $"SELECT * FROM {BenchmarkData.TableName(1_000_000)}";
            using IArrowArrayStream stream = statement.ExecuteQuery().Stream!;
            while (stream.ReadNextRecordBatchAsync().AsTask().GetAwaiter().GetResult() is RecordBatch batch)
            {
                batches.Add(batch);
            }
        }

        // The target columns as the server describes the table, as SqlServerBulkIngest gets them.
        session.SendSqlBatch($"SELECT TOP 0 * FROM {BenchmarkData.TableName(1_000_000)}");
        TdsColumn[] columns = session.ReadUntilResultSet()!;
        session.DrainResponse(columns);
        Schema schema = batches[0].Schema;
        ColumnEncoder Encoder(int i) => EncoderFactory.Create(columns[i].ToBulkTarget(), schema.FieldsList[i], i, session.DefaultCollation);

        Console.WriteLine($"Encoding {batches.Sum(b => b.Length):N0} rows into Stream.Null, {iterations} runs (median)");
        Console.WriteLine($"{"columns",-22} {"Mcycles",10} {"ms",8} {"ns/value",9}");
        Measure("all 17", Enumerable.Range(0, schema.FieldsList.Count).Select(Encoder).ToArray(), batches, iterations);
        for (int i = 0; i < schema.FieldsList.Count; i++)
        {
            Measure(schema.FieldsList[i].Name, [Encoder(i)], batches, iterations);
        }
        batches.ForEach(b => b.Dispose());
    }

    private static void Measure(string name, ColumnEncoder[] encoders, List<RecordBatch> batches, int iterations)
    {
        var writer = new PacketWriter(Stream.Null, TdsConstants.MaxPacketSize);
        var cycles = new List<double>();
        var ms = new List<double>();
        long values = 0;
        for (int run = 0; run <= iterations; run++)
        {
            ulong c0 = Cycles();
            long t0 = Stopwatch.GetTimestamp();
            writer.BeginMessage(PacketType.BulkLoad);
            foreach (RecordBatch batch in batches)
            {
                foreach (ColumnEncoder encoder in encoders)
                {
                    encoder.Bind(batch.Column(encoder.SourceIndex));
                }
                int length = batch.Length;
                for (int row = 0; row < length; row++)
                {
                    writer.WriteByte((byte)TokenType.Row);
                    for (int c = 0; c < encoders.Length; c++)
                    {
                        encoders[c].Write(writer, row);
                    }
                }
            }
            writer.EndMessage();
            if (run > 0)
            {
                cycles.Add((Cycles() - c0) / 1e6);
                ms.Add(Stopwatch.GetElapsedTime(t0).TotalMilliseconds);
            }
            values = batches.Sum(b => (long)b.Length) * encoders.Length;
        }
        foreach (ColumnEncoder encoder in encoders)
        {
            encoder.Release();
        }
        double medianMs = Median(ms);
        Console.WriteLine($"{name,-22} {Median(cycles),10:F1} {medianMs,8:F1} {medianMs * 1e6 / values,9:F2}");
    }

    private static double Median(List<double> values)
    {
        double[] sorted = values.Order().ToArray();
        return sorted.Length % 2 == 1 ? sorted[sorted.Length / 2] : (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2;
    }
}
