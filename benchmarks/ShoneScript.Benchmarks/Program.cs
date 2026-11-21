// Copyright (c) 2026 Shone
// SPDX-License-Identifier: MIT

using BenchmarkDotNet.Running;

var summaries = BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
return summaries.Any(summary => summary.HasCriticalValidationErrors ||
    summary.Reports.Any(report => !report.Success)) ? 1 : 0;
