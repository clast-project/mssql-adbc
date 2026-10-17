// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

#if !NET
using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;

namespace System
{
    /// <summary>
    /// Members of newer .NET that the driver uses, for netstandard2.0, as
    /// extension members so the code that calls them reads the same on both.
    /// They aim at correctness rather than speed: netstandard2.0 serves hosts
    /// (.NET Framework, older .NET) where the net10.0 build cannot load.
    /// </summary>
    internal static class BclExtensions
    {
        extension(BitConverter)
        {
            public static unsafe int SingleToInt32Bits(float value) => *(int*)&value;

            public static unsafe float Int32BitsToSingle(int value) => *(float*)&value;
        }

        extension(BinaryPrimitives)
        {
            public static float ReadSingleLittleEndian(ReadOnlySpan<byte> source) =>
                BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(source));

            public static double ReadDoubleLittleEndian(ReadOnlySpan<byte> source) =>
                BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(source));

            public static void WriteSingleLittleEndian(Span<byte> destination, float value) =>
                BinaryPrimitives.WriteInt32LittleEndian(destination, BitConverter.SingleToInt32Bits(value));

            public static void WriteDoubleLittleEndian(Span<byte> destination, double value) =>
                BinaryPrimitives.WriteInt64LittleEndian(destination, BitConverter.DoubleToInt64Bits(value));
        }

        extension(OperatingSystem)
        {
            public static bool IsWindows() => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

            public static bool IsLinux() => RuntimeInformation.IsOSPlatform(OSPlatform.Linux);

            public static bool IsMacOS() => RuntimeInformation.IsOSPlatform(OSPlatform.OSX);
        }

        extension(Environment)
        {
            public static int ProcessId
            {
                get
                {
                    using Process process = Process.GetCurrentProcess();
                    return process.Id;
                }
            }
        }

        extension(ObjectDisposedException)
        {
            public static void ThrowIf(bool condition, object instance)
            {
                if (condition)
                {
                    throw new ObjectDisposedException(instance.GetType().FullName);
                }
            }
        }

        extension(ArgumentNullException)
        {
            public static void ThrowIfNull(object? argument, string? paramName = null)
            {
                if (argument is null)
                {
                    throw new ArgumentNullException(paramName);
                }
            }
        }

        extension(Guid guid)
        {
            public bool TryWriteBytes(Span<byte> destination)
            {
                if (destination.Length < 16)
                {
                    return false;
                }
                guid.ToByteArray().CopyTo(destination);
                return true;
            }
        }

        extension(string value)
        {
            public bool StartsWith(char c) => value.Length > 0 && value[0] == c;

            public string[] Split(string separator, StringSplitOptions options) => value.Split([separator], options);
        }

        extension(Task task)
        {
            public bool IsCompletedSuccessfully => task.Status == TaskStatus.RanToCompletion;
        }

        extension(Encoding encoding)
        {
            public unsafe string GetString(ReadOnlySpan<byte> bytes)
            {
                if (bytes.IsEmpty)
                {
                    return string.Empty;
                }
                fixed (byte* p = bytes)
                {
                    return encoding.GetString(p, bytes.Length);
                }
            }

            public unsafe int GetCharCount(ReadOnlySpan<byte> bytes)
            {
                if (bytes.IsEmpty)
                {
                    return 0;
                }
                fixed (byte* p = bytes)
                {
                    return encoding.GetCharCount(p, bytes.Length);
                }
            }

            public unsafe int GetChars(ReadOnlySpan<byte> bytes, Span<char> chars)
            {
                if (bytes.IsEmpty)
                {
                    return 0;
                }
                fixed (byte* p = bytes)
                fixed (char* c = chars)
                {
                    return encoding.GetChars(p, bytes.Length, c, chars.Length);
                }
            }

            public unsafe int GetByteCount(ReadOnlySpan<char> chars)
            {
                if (chars.IsEmpty)
                {
                    return 0;
                }
                fixed (char* c = chars)
                {
                    return encoding.GetByteCount(c, chars.Length);
                }
            }

            public unsafe int GetBytes(ReadOnlySpan<char> chars, Span<byte> bytes)
            {
                if (chars.IsEmpty)
                {
                    return 0;
                }
                fixed (char* c = chars)
                fixed (byte* p = bytes)
                {
                    return encoding.GetBytes(c, chars.Length, p, bytes.Length);
                }
            }

            public int GetBytes(string s, Span<byte> bytes) => encoding.GetBytes(s.AsSpan(), bytes);
        }

        extension(UdpClient client)
        {
            /// <summary>
            /// A receive that stops waiting when cancelled. The receive itself
            /// cannot be cancelled (it ends when the client is disposed), so a
            /// later call waits on the same one: a new receive would leave the
            /// next datagram to the abandoned one.
            /// </summary>
            public async ValueTask<UdpReceiveResult> ReceiveAsync(CancellationToken cancellationToken)
            {
                Task<UdpReceiveResult> receive;
                lock (s_pendingReceives)
                {
                    if (!s_pendingReceives.TryGetValue(client, out receive!) || receive.IsCompleted)
                    {
                        s_pendingReceives.Remove(client);
                        receive = client.ReceiveAsync();
                        _ = receive.ContinueWith(static t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
                        s_pendingReceives.Add(client, receive);
                    }
                }
                var cancelled = new TaskCompletionSource<bool>();
                using (cancellationToken.Register(() => cancelled.TrySetResult(true)))
                {
                    if (await Task.WhenAny(receive, cancelled.Task).ConfigureAwait(false) != receive)
                    {
                        throw new OperationCanceledException(cancellationToken);
                    }
                }
                return await receive.ConfigureAwait(false);
            }
        }

        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<UdpClient, Task<UdpReceiveResult>> s_pendingReceives = new();

        extension(Socket socket)
        {
            /// <summary>A connect that closes the socket when cancelled.</summary>
            public async ValueTask ConnectAsync(string host, int port, CancellationToken cancellationToken)
            {
                using (cancellationToken.Register(static s => ((Socket)s!).Dispose(), socket))
                {
                    try
                    {
                        await SocketTaskExtensions.ConnectAsync(socket, host, port).ConfigureAwait(false);
                    }
                    catch (Exception) when (cancellationToken.IsCancellationRequested)
                    {
                        throw new OperationCanceledException(cancellationToken);
                    }
                }
            }
        }
    }
}

namespace System.Collections.Generic
{
    internal static class CollectionExtensions
    {
        extension<T>(Queue<T> queue)
        {
            public bool TryDequeue(out T result)
            {
                if (queue.Count == 0)
                {
                    result = default!;
                    return false;
                }
                result = queue.Dequeue();
                return true;
            }
        }

        extension<T>(Stack<T> stack)
        {
            public bool TryPop(out T result)
            {
                if (stack.Count == 0)
                {
                    result = default!;
                    return false;
                }
                result = stack.Pop();
                return true;
            }
        }

        extension<TKey, TValue>(Dictionary<TKey, TValue> dictionary) where TKey : notnull
        {
            public bool TryAdd(TKey key, TValue value)
            {
                if (dictionary.ContainsKey(key))
                {
                    return false;
                }
                dictionary.Add(key, value);
                return true;
            }
        }
    }
}

namespace System.Linq
{
    internal static class EnumerableExtensions
    {
        extension<T>(IEnumerable<T> source)
        {
            public HashSet<T> ToHashSet() => new(source);

            public IEnumerable<(T First, TSecond Second)> Zip<TSecond>(IEnumerable<TSecond> second) =>
                source.Zip(second, static (a, b) => (a, b));
        }
    }
}

namespace System.Text
{
    /// <summary>.NET 8's ASCII check, for netstandard2.0.</summary>
    internal static class Ascii
    {
        public static bool IsValid(ReadOnlySpan<byte> value)
        {
            foreach (byte b in value)
            {
                if (b >= 0x80)
                {
                    return false;
                }
            }
            return true;
        }
    }
}

namespace System.Text.Unicode
{
    /// <summary>
    /// .NET Core's UTF-8/UTF-16 transcoding, over <see cref="Encoding.UTF8"/>
    /// (which replaces invalid sequences), for netstandard2.0. Callers size the
    /// destination for the worst case.
    /// </summary>
    internal static class Utf8
    {
        public static OperationStatus ToUtf16(ReadOnlySpan<byte> source, Span<char> destination, out int bytesRead, out int charsWritten, bool replaceInvalidSequences = true, bool isFinalBlock = true)
        {
            bytesRead = source.Length;
            charsWritten = Encoding.UTF8.GetChars(source, destination);
            return OperationStatus.Done;
        }

        public static OperationStatus FromUtf16(ReadOnlySpan<char> source, Span<byte> destination, out int charsRead, out int bytesWritten, bool replaceInvalidSequences = true, bool isFinalBlock = true)
        {
            charsRead = source.Length;
            bytesWritten = Encoding.UTF8.GetBytes(source, destination);
            return OperationStatus.Done;
        }
    }
}
#endif
