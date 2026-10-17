// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

#if !NET
// Types the compiler needs for newer language features, which netstandard2.0 lacks.

namespace System.Runtime.CompilerServices
{
    /// <summary>Lets records and init-only setters compile.</summary>
    internal static class IsExternalInit
    {
    }
}

namespace System.Runtime.Versioning
{
    /// <summary>Platform annotations; informational only off .NET 5+.</summary>
    [AttributeUsage(AttributeTargets.All, AllowMultiple = true, Inherited = false)]
    internal sealed class SupportedOSPlatformAttribute(string platformName) : Attribute
    {
        public string PlatformName { get; } = platformName;
    }
}
#endif
