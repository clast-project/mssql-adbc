#!/usr/bin/env bash
# Copyright (c) clast-project. All rights reserved.
# Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

# Publishes the driver as a NativeAOT shared library and runs the interop tests
# against it. The native library only exists after `dotnet publish`, so these
# tests can't run in the normal `dotnet test` pass: they find the published
# library through ADBC_TEST_AOT_FIXTURE_PATH. With MSSQL_TEST_CONFIG_FILE set,
# the real-server tests run too.
#
# Usage: csharp_aot.sh <workspace> <rid>
#   <rid>  e.g. win-x64, linux-x64, linux-arm64, osx-arm64
#
# On Windows, NativeAOT links with MSVC: run from a Developer shell, or with
# vswhere.exe (in "Microsoft Visual Studio\Installer") on PATH.

set -ex

workspace=${1}
rid=${2}

native_project=${workspace}/src/Clast.Drivers.SqlServer.Native/Clast.Drivers.SqlServer.Native.csproj
tests_project=${workspace}/test/Clast.Drivers.SqlServer.Native.Tests/Clast.Drivers.SqlServer.Native.Tests.csproj
publish_dir=${workspace}/artifacts/aot-fixture

# 1. NativeAOT-publish the shared library for this RID.
dotnet publish "${native_project}" -c Release -r "${rid}" -o "${publish_dir}"

# 2. Find it. NativeAOT names it <assembly>.{so,dylib,dll}, without a "lib"
#    prefix by default (opt-in through $(_UseNativeLibPrefix), which a future
#    SDK could flip), so look rather than hardcode. The output holds exactly
#    one shared library.
fixture=$(find "${publish_dir}" -maxdepth 1 -type f \( -name '*.so' -o -name '*.dylib' -o -name '*.dll' \) | head -n 1)
if [ -z "${fixture}" ]; then
  echo "No NativeAOT shared library (*.so/*.dylib/*.dll) found under ${publish_dir}" >&2
  exit 1
fi

# 3. Run the interop tests against it.
ADBC_TEST_AOT_FIXTURE_PATH="${fixture}" \
  dotnet test "${tests_project}" -c Release --logger "console;verbosity=normal"
