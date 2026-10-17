#!/usr/bin/env bash
# Copyright (c) clast-project. All rights reserved.
# Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

# Assembles the multi-RID NativeAOT driver package. Each RID's shared library
# is built on its own OS runner by csharp_aot.sh and uploaded as an artifact
# named aot-native-<rid>; this stages them under runtimes/<rid>/native/ and
# runs `dotnet pack -p:IsPackagingPipeline=true` to make one .nupkg.
# NativeAOT can't cross-compile, which is why the libraries are gathered here
# rather than built in one pass.
#
# Usage: csharp_aot_pack.sh <workspace> <artifacts-dir> <out-dir>
#   <artifacts-dir>  the downloaded artifacts, one directory per RID:
#                    <artifacts-dir>/aot-native-<rid>/<lib>
#   <out-dir>        where the .nupkg is written

set -ex

workspace=${1}
artifacts_dir=${2}
out_dir=${3}

project_dir=${workspace}/src/Clast.Drivers.SqlServer.Native
project=${project_dir}/Clast.Drivers.SqlServer.Native.csproj
runtimes_dir=${project_dir}/runtimes

# Stage each library at runtimes/<rid>/native/<lib>, the layout the pack step
# (and NuGet) expects. Start clean so a rerun carries no stale RIDs.
rm -rf "${runtimes_dir}"
for dir in "${artifacts_dir}"/aot-native-*; do
  [ -d "${dir}" ] || continue
  rid=${dir##*/aot-native-}
  mkdir -p "${runtimes_dir}/${rid}/native"
  cp "${dir}"/* "${runtimes_dir}/${rid}/native/"
done

dotnet pack "${project}" -c Release -p:IsPackagingPipeline=true -o "${out_dir}"
