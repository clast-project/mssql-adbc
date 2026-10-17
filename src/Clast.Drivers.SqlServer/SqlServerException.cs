// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Apache.Arrow.Adbc;

namespace Clast.Drivers.SqlServer;

/// <summary>An ERROR or INFO message from the server.</summary>
public sealed record SqlServerError(
    int Number,
    byte State,
    byte Class,
    string Message,
    string ServerName,
    string ProcedureName,
    int LineNumber);

/// <summary>
/// One or more errors returned by the server. The first error determines the
/// message and status code; all of them are in <see cref="Errors"/>.
/// </summary>
public sealed class SqlServerException : AdbcException
{
    internal SqlServerException(IReadOnlyList<SqlServerError> errors)
        : base(FormatMessage(errors), MapStatus(errors[0]))
    {
        Errors = errors;
    }

    public IReadOnlyList<SqlServerError> Errors { get; }

    public int Number => Errors[0].Number;

    public override int NativeError => Errors[0].Number;

    private static string FormatMessage(IReadOnlyList<SqlServerError> errors)
    {
        if (errors.Count == 1)
        {
            return errors[0].Message;
        }
        return string.Join(Environment.NewLine, errors.Select(e => e.Message));
    }

    private static AdbcStatusCode MapStatus(SqlServerError error) => error.Number switch
    {
        18456 or 18452 or 18470 or 18486 or 18487 or 18488 => AdbcStatusCode.Unauthenticated,
        229 or 230 or 262 or 297 or 300 => AdbcStatusCode.Unauthorized,
        208 or 207 or 2812 or 4060 => AdbcStatusCode.NotFound,
        2714 or 1801 => AdbcStatusCode.AlreadyExists,
        2627 or 2601 or 547 or 515 => AdbcStatusCode.IntegrityError,
        102 or 105 or 156 or 170 or 8114 or 245 or 241 or 8115 or 220 or 8152 or 2628 => AdbcStatusCode.InvalidArgument,
        3980 or 3617 => AdbcStatusCode.Cancelled,
        1205 => AdbcStatusCode.InvalidState,
        -2 => AdbcStatusCode.Timeout,
        _ => error.Class >= 20 ? AdbcStatusCode.IOError : AdbcStatusCode.UnknownError,
    };
}
