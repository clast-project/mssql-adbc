// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Net;
using System.Net.Sockets;
using System.Text;
using Apache.Arrow.Adbc;

namespace Clast.Drivers.SqlServer.Tds;

/// <summary>
/// Resolves a named instance with the SQL Server Resolution Protocol
/// ([MC-SQLR]): a CLNT_UCAST_INST datagram to the SQL Server Browser on UDP
/// 1434, answered by an SVR_RESP listing the instance's endpoints.
/// </summary>
internal static class SqlBrowser
{
    private const byte ClientUnicastInstance = 0x04;
    private const byte ServerResponse = 0x05;

    /// <summary>The browser's UDP port; tests point it at a fake browser.</summary>
    internal static int Port = 1434;

    /// <summary>How long to wait for a reply before sending the request again.</summary>
    internal static TimeSpan AttemptTimeout = TimeSpan.FromSeconds(1);

    /// <summary>An instance's endpoints, as the browser reports them.</summary>
    /// <param name="PipeName">The pipe name after <c>\pipe\</c>, for example <c>MSSQL$SQLEXPRESS\sql\query</c>.</param>
    public sealed record Instance(string Name, int? TcpPort, string? PipeName);

    /// <summary>
    /// Asks the browser on <paramref name="host"/> for <paramref name="instance"/>.
    /// The request goes to every address of the host at once and is repeated
    /// each <see cref="AttemptTimeout"/> until a reply arrives or
    /// <paramref name="timeout"/> passes.
    /// </summary>
    public static Instance Resolve(string host, string instance, TimeSpan timeout)
    {
        byte[] request = CreateRequest(instance);
        IPAddress[] addresses = IPAddress.TryParse(host.Trim('[', ']'), out IPAddress? address) ? [address] : Dns.GetHostAddresses(host);

        DateTime deadline = DateTime.UtcNow + timeout;
        var clients = addresses.Select(a => new UdpClient(a.AddressFamily)).ToArray();
        try
        {
            while (true)
            {
                TimeSpan remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero)
                {
                    break;
                }
                using var attempt = new CancellationTokenSource(remaining < AttemptTimeout ? remaining : AttemptTimeout);
                var receives = new List<Task<UdpReceiveResult>>();
                for (int i = 0; i < addresses.Length; i++)
                {
                    try
                    {
                        clients[i].Send(request, request.Length, new IPEndPoint(addresses[i], Port));
                        receives.Add(clients[i].ReceiveAsync(attempt.Token).AsTask());
                    }
                    catch (SocketException)
                    {
                        // This address is unreachable; others may answer.
                    }
                }

                while (receives.Count > 0)
                {
                    Task<UdpReceiveResult> done;
                    try
                    {
                        done = Task.WhenAny(receives).GetAwaiter().GetResult();
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    receives.Remove(done);
                    if (done.IsCompletedSuccessfully && Parse(done.Result.Buffer, instance) is Instance found)
                    {
                        attempt.Cancel();
                        return found;
                    }
                    // A failed receive (port unreachable, cancelled) or a reply for another instance.
                }
            }
        }
        finally
        {
            foreach (UdpClient client in clients)
            {
                client.Dispose();
            }
        }

        throw new AdbcException(
            $"Could not resolve instance '{instance}' on '{host}': no reply from the SQL Server Browser on UDP {Port}. " +
            $"Check that the SQL Browser service is running and the instance name is right, or give the port instead (for example '{host},1433')",
            AdbcStatusCode.IOError);
    }

    internal static byte[] CreateRequest(string instance)
    {
        byte[] name = Encoding.UTF8.GetBytes(instance);
        var request = new byte[name.Length + 2];
        request[0] = ClientUnicastInstance;
        name.CopyTo(request, 1);
        return request; // ends with the terminating NUL
    }

    /// <summary>
    /// Parses an SVR_RESP: 0x05, a little-endian length, then
    /// <c>ServerName;S;InstanceName;I;IsClustered;No;Version;V;tcp;1433;np;\\S\pipe\sql\query;;</c>
    /// for one or more instances. Returns the named instance, or null.
    /// </summary>
    internal static Instance? Parse(ReadOnlySpan<byte> response, string instance)
    {
        if (response.Length < 3 || response[0] != ServerResponse || response[1] + (response[2] << 8) != response.Length - 3)
        {
            return null;
        }
        string text = Encoding.UTF8.GetString(response.Slice(3));
        foreach (string record in text.Split(";;", StringSplitOptions.RemoveEmptyEntries))
        {
            string[] parts = record.Split(';');
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i + 1 < parts.Length; i += 2)
            {
                values.TryAdd(parts[i], parts[i + 1]);
            }
            if (!values.TryGetValue("InstanceName", out string? name) || !string.Equals(name, instance, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            int? port = values.TryGetValue("tcp", out string? tcp) && int.TryParse(tcp, out int p) && p is > 0 and <= 65535 ? p : null;
            string? pipe = null;
            if (values.TryGetValue("np", out string? np))
            {
                const string marker = @"\pipe\";
                int index = np.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                pipe = index >= 0 ? np.Substring(index + marker.Length) : null;
            }
            return new Instance(name, port, pipe);
        }
        return null;
    }
}
