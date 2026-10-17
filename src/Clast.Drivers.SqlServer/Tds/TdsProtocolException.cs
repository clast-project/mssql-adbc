// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Apache.Arrow.Adbc;

namespace Clast.Drivers.SqlServer.Tds;

/// <summary>
/// The byte stream from the server violated the protocol, or the connection
/// was lost. The session cannot be used afterwards.
/// </summary>
internal sealed class TdsProtocolException : AdbcException
{
    public TdsProtocolException(string message, Exception? innerException = null)
        : base(message, AdbcStatusCode.IOError, innerException!)
    {
    }
}
