// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Reflection;
using BenchmarkDotNet.Running;
using Microsoft.AspNetCore.OutputCaching.Benchmark;

#if DEBUG
var obj = new EndToEndBenchmarks();
await obj.InitAsync(); // validation etc
obj.Cleanup();
#else
BenchmarkRunner.Run(Assembly.GetExecutingAssembly(), args: args);
#endif
