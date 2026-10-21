// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Diagnostics;
using System.Globalization;

namespace Clast.Drivers.SqlServer.Benchmarks;

/// <summary>
/// Wall time and this process's CPU time (all threads, user and kernel) per
/// full read or ingest, for each driver, interleaved. It needs nothing from the
/// server, so it suits remote services, where wall time is mostly the
/// service's and the network's and the drivers differ in what they cost the client.
/// <c>--analyze client [iterations] [read|ingest] [batch sizes]</c>; for ingest,
/// a comma-separated list of batch sizes (0 for one batch) measures each.
/// </summary>
internal static class ClientCpu
{
    public static void Run(int iterations, string workload, int[] batchSizes)
    {
        var cases = new List<(string Name, Action Run, Action Cleanup, List<(double Wall, double Cpu)> Samples)>();
        foreach (string driver in ReadBenchmarks.Drivers())
        {
            if (workload == "ingest" && driver == "Go" && BenchmarkData.Warehouse)
            {
                // It bulk-loads a warehouse only through Azure storage (mssql.ingest.storage_account).
                Console.Error.WriteLine("Go: skipped (warehouse ingest needs a storage account)");
                continue;
            }
            if (workload == "ingest")
            {
                foreach (int batchSize in batchSizes)
                {
                    if (batchSize > 0 && driver == "Go")
                    {
                        continue;
                    }
                    var ingest = new IngestBenchmarks { Rows = 1_000_000, Driver = driver, TableLock = false, BatchSize = batchSize };
                    ingest.Setup();
                    string name = batchSizes.Length > 1 ? $"{driver}/{batchSize}" : driver;
                    cases.Add((name, () => ingest.Ingest(), ingest.Cleanup, new()));
                }
            }
            else
            {
                var read = new ReadBenchmarks { Rows = 1_000_000, Driver = driver, Encryption = "full" };
                read.Setup();
                cases.Add((driver, () => read.ReadAll().GetAwaiter().GetResult(), read.Cleanup, new()));
            }
        }

        Process process = Process.GetCurrentProcess();
        foreach (var c in cases)
        {
            c.Run();
        }
        for (int i = 0; i < iterations; i++)
        {
            foreach (var c in cases)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                process.Refresh();
                TimeSpan cpu0 = process.TotalProcessorTime;
                long start = Stopwatch.GetTimestamp();
                c.Run();
                double wall = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                process.Refresh();
                c.Samples.Add((wall, (process.TotalProcessorTime - cpu0).TotalMilliseconds));
            }
            Console.Error.Write('.');
        }
        Console.Error.WriteLine();

        Console.WriteLine($"{workload}, 1M rows, {iterations} iterations (median, min-max)");
        Console.WriteLine($"{"Driver",-16} {"Wall ms",22} {"Client CPU ms",22}");
        foreach (var c in cases)
        {
            Console.WriteLine($"{c.Name,-16} {Stats(c.Samples.Select(s => s.Wall)),22} {Stats(c.Samples.Select(s => s.Cpu)),22}");
            c.Cleanup();
        }
    }

    private static string Stats(IEnumerable<double> values)
    {
        double[] sorted = values.Order().ToArray();
        double median = sorted.Length % 2 == 1 ? sorted[sorted.Length / 2] : (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2;
        return string.Create(CultureInfo.InvariantCulture, $"{median:F0} ({sorted[0]:F0}-{sorted[^1]:F0})");
    }
}
