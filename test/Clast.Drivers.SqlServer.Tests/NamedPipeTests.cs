// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.IO.Pipes;
using Apache.Arrow.Adbc;
using Xunit;

namespace Clast.Drivers.SqlServer.Tests;

/// <summary>Named-pipe transport behavior that needs no SQL Server.</summary>
public sealed class NamedPipeTests
{
    /// <summary>
    /// A server that closes a new pipe connection before answering PRELOGIN
    /// (LocalDB does so now and then) gets three more tries on new connections.
    /// </summary>
    [SkippableFact]
    public async Task RetriesAPipeClosedBeforePreLogin()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Named pipes are Windows-only");
        string name = "clast-test-" + Guid.NewGuid().ToString("N");
        int accepted = 0;
        using var stop = new CancellationTokenSource();
        Task server = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                using var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                try
                {
                    await pipe.WaitForConnectionAsync(stop.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                Interlocked.Increment(ref accepted);
                // Take the PRELOGIN, then hang up without answering.
                Assert.True(await pipe.ReadAsync(new byte[512], 0, 512) > 0);
                pipe.Disconnect();
            }
        });

        var options = new Dictionary<string, string>
        {
            [SqlServerParameters.Server] = $@"np:\\.\pipe\{name}",
            [SqlServerParameters.Username] = "user",
            [SqlServerParameters.Password] = "password",
            [SqlServerParameters.Encrypt] = "optional",
            [SqlServerParameters.ConnectTimeout] = "5",
        };
        AdbcException ex = Assert.ThrowsAny<AdbcException>(() => new SqlServerDriver().Open(options).Connect(null));
        Assert.Contains("closed the connection", ex.Message);
        Assert.Equal(4, Volatile.Read(ref accepted));

        stop.Cancel();
        await server;
    }
}
