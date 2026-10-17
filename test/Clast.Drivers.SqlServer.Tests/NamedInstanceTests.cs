// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Apache.Arrow;
using Apache.Arrow.Adbc;
using Apache.Arrow.Ipc;
using Clast.Drivers.SqlServer.Tds;
using Xunit;

namespace Clast.Drivers.SqlServer.Tests;

/// <summary>
/// Named instances. The test server has only the default instance and no
/// SQL Server Browser, so a fake browser answers for instance names that map
/// to the real server's endpoints.
/// </summary>
public sealed class NamedInstanceTests : IDisposable
{
    private readonly FakeBrowser _browser = new();
    private readonly int _savedPort = SqlBrowser.Port;
    private readonly TimeSpan _savedTimeout = SqlBrowser.AttemptTimeout;

    public NamedInstanceTests()
    {
        SqlBrowser.Port = _browser.Port;
        SqlBrowser.AttemptTimeout = TimeSpan.FromMilliseconds(300);
    }

    public void Dispose()
    {
        SqlBrowser.Port = _savedPort;
        SqlBrowser.AttemptTimeout = _savedTimeout;
        _browser.Dispose();
    }

    private static AdbcConnection Connect(string server, Action<Dictionary<string, string>>? configure = null)
    {
        SqlServerTestConfiguration config = SqlServerTestConfiguration.Load();
        var options = new Dictionary<string, string>(config.Options) { [SqlServerParameters.Server] = server };
        configure?.Invoke(options);
        return new SqlServerDriver().Open(options).Connect(null);
    }

    private static string Reply(params string[] records) => string.Concat(records.Select(r => r + ";;"));

    private static string Record(string instance, int? tcp, string? pipe) =>
        $"ServerName;TEST;InstanceName;{instance};IsClustered;No;Version;17.0.1000.7" +
        (tcp != null ? $";tcp;{tcp}" : "") + (pipe != null ? $@";np;\\TEST\pipe\{pipe}" : "");

    [Fact]
    public void RequestIsClientUnicastInstance()
    {
        Assert.Equal([0x04, (byte)'F', (byte)'A', (byte)'K', (byte)'E', 0x00], SqlBrowser.CreateRequest("FAKE"));
    }

    [Fact]
    public void ParsesReplies()
    {
        byte[] reply = FakeBrowser.Encode(Reply(Record("OTHER", 50000, null), Record("Fake", 50123, @"MSSQL$FAKE\sql\query")));
        SqlBrowser.Instance instance = SqlBrowser.Parse(reply, "FAKE")!;
        Assert.Equal(50123, instance.TcpPort);
        Assert.Equal(@"MSSQL$FAKE\sql\query", instance.PipeName);

        Assert.Null(SqlBrowser.Parse(reply, "MISSING"));
        Assert.Null(SqlBrowser.Parse([0x05, 0x10, 0x00, (byte)'x'], "FAKE")); // length mismatch
        Assert.Null(SqlBrowser.Parse([0x04, 0x00, 0x00], "FAKE"));
        Assert.Null(SqlBrowser.Parse(FakeBrowser.Encode(Reply(Record("FAKE", null, null))), "FAKE")!.TcpPort);
    }

    [SkippableFact]
    public void ResolvesTcpPortThroughBrowser()
    {
        _browser.Respond = _ => Reply(Record("FAKE", 1433, null));
        using AdbcConnection connection = Connect(@"localhost\FAKE");
        Assert.True(((SqlServerConnection)connection).Session.ServerVersion.Major >= 11);
        Assert.Equal(SqlBrowser.CreateRequest("FAKE"), _browser.Requests.First());
    }

    [SkippableFact]
    public void FallsBackToReportedPipe()
    {
        // TCP disabled for the instance: the browser lists only its pipe.
        _browser.Respond = _ => Reply(Record("PIPED", null, @"sql\query"));
        using (AdbcConnection connection = Connect(@".\PIPED"))
        {
            Assert.Equal(@"sql\query", ((SqlServerConnection)connection).Session.Settings.PipeName);
        }

        // Unless TCP was asked for.
        var ex = Assert.Throws<AdbcException>(() => Connect(@"tcp:localhost\PIPED"));
        Assert.Contains("does not accept TCP", ex.Message);
    }

    [SkippableFact]
    public void RetriesLostDatagrams()
    {
        int calls = 0;
        _browser.Respond = _ => Interlocked.Increment(ref calls) <= 2 ? null : Reply(Record("LOSSY", 1433, null));
        using AdbcConnection connection = Connect(@"127.0.0.1\LOSSY");
        Assert.True(calls >= 3);
    }

    [SkippableFact]
    public void SilentBrowserFailsWithinConnectTimeout()
    {
        _browser.Respond = _ => null;
        var watch = Stopwatch.StartNew();
        var ex = Assert.Throws<AdbcException>(() => Connect(@"localhost\NOBODY", o => o[SqlServerParameters.ConnectTimeout] = "2"));
        Assert.Equal(AdbcStatusCode.IOError, ex.Status);
        Assert.Contains("SQL Server Browser", ex.Message);
        Assert.InRange(watch.Elapsed.TotalSeconds, 1.5, 5);
    }

    [SkippableTheory]
    [InlineData(@"localhost\MSSQLSERVER")]
    [InlineData(@".\mssqlserver")]
    [InlineData(@"(local)\MSSQLSERVER")]
    [InlineData(@"tcp:localhost\MSSQLSERVER")]
    [InlineData(@"localhost\ANYTHING,1433")]
    [InlineData(@"np:localhost\MSSQLSERVER")]
    [InlineData(@"np:.")]
    public void DefaultInstanceAndExplicitPortsNeedNoBrowser(string server)
    {
        _browser.Respond = _ => null;
        using AdbcConnection connection = Connect(server);
        Assert.True(((SqlServerConnection)connection).Session.ServerVersion.Major >= 11);
        Assert.Empty(_browser.Requests);
    }

    [SkippableFact]
    public void ResolutionIsSharedByParallelReadSessions()
    {
        _browser.Respond = _ => Reply(Record("FAKE", 1433, null));
        using AdbcConnection connection = Connect(@"localhost\FAKE", o => o[SqlServerParameters.ParallelReads] = "4");
        using AdbcStatement statement = connection.CreateStatement();
        statement.SqlQuery = "SELECT CAST(n AS bigint) AS k FROM (SELECT TOP (4000) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS n FROM sys.all_objects a CROSS JOIN sys.all_objects b) AS t";
        statement.SetOption(SqlServerParameters.PartitionColumn, "k");
        using IArrowArrayStream stream = statement.ExecuteQuery().Stream!;
        Assert.IsType<SqlServerParallelQueryStream>(stream);
        long rows = 0;
        while (stream.ReadNextRecordBatchAsync().AsTask().GetAwaiter().GetResult() is RecordBatch batch)
        {
            rows += batch.Length;
            batch.Dispose();
        }
        Assert.Equal(4000, rows);
        Assert.Single(_browser.Requests);
    }

    [Theory]
    [InlineData(@"localhost\")]
    [InlineData(@"localhost\a\b")]
    [InlineData(@"np:localhost\")]
    public void MalformedInstanceIsInvalid(string server)
    {
        var ex = Assert.Throws<AdbcException>(() => SqlServerConnectionSettings.Parse(new Dictionary<string, string> { [SqlServerParameters.Server] = server }));
        Assert.Equal(AdbcStatusCode.InvalidArgument, ex.Status);
    }

    /// <summary>Answers CLNT_UCAST_INST requests on 127.0.0.1 with whatever <see cref="Respond"/> returns (null: stay silent).</summary>
    private sealed class FakeBrowser : IDisposable
    {
        private readonly UdpClient _udp = new(new IPEndPoint(IPAddress.Loopback, 0));
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _loop;

        public FakeBrowser()
        {
            _loop = Task.Run(Loop);
        }

        public int Port => ((IPEndPoint)_udp.Client.LocalEndPoint!).Port;

        public Func<byte[], string?> Respond { get; set; } = _ => null;

        public System.Collections.Concurrent.ConcurrentQueue<byte[]> Requests { get; } = new();

        public static byte[] Encode(string reply)
        {
            byte[] text = Encoding.ASCII.GetBytes(reply);
            var packet = new byte[text.Length + 3];
            packet[0] = 0x05;
            packet[1] = (byte)text.Length;
            packet[2] = (byte)(text.Length >> 8);
            text.CopyTo(packet, 3);
            return packet;
        }

        private async Task Loop()
        {
            while (!_stop.IsCancellationRequested)
            {
                UdpReceiveResult request;
                try
                {
                    request = await _udp.ReceiveAsync(_stop.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (SocketException)
                {
                    continue;
                }
                Requests.Enqueue(request.Buffer);
                if (Respond(request.Buffer) is string reply)
                {
                    byte[] packet = Encode(reply);
                    await _udp.SendAsync(packet, request.RemoteEndPoint);
                }
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            try
            {
                _loop.GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
            }
            _udp.Dispose();
        }
    }
}
