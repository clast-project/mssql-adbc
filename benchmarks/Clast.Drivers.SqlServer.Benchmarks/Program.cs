// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using BenchmarkDotNet.Running;
using Clast.Drivers.SqlServer.Benchmarks;

if (args.Length > 0 && args[0] == "--analyze")
{
    Analysis.Run(args);
    return;
}

BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
