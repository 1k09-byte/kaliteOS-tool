// ==============================================================================
// Copyright (c) 2026 kaliteConfig
// All rights reserved.
//
// This software and associated documentation files are provided freely for end-users
// to download and use. However, the source code remains strictly proprietary. 
// You may not copy, reproduce, modify, merge, reverse-engineer, publish, distribute, 
// sublicense, or sell copies of the source code in any form, in whole or in part,
// without the express written permission of the copyright holder.
// ==============================================================================
using System;
using System.Collections.Generic;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;

namespace kaliteConfig.Services;

/// <summary>ICMP latency runs over managed Ping (async, cancellable, no child process).
/// Math lives in NetParsing and is unit-tested; this only gathers RTTs.</summary>
public static class NetLatency
{
    public sealed record Run(NetPingSummary Summary, List<double> Samples);

    public static async Task<Run> PingAsync(
        string host, int count, int intervalMs,
        Action<int, int>? progress = null, CancellationToken ct = default)
    {
        var samples = new List<double>();
        int sent = 0;
        using var ping = new Ping();
        var opts = new PingOptions(128, true);
        var payload = new byte[32];
        for (int i = 0; i < count; i++)
        {
            ct.ThrowIfCancellationRequested();
            sent++;
            progress?.Invoke(sent, count);
            try
            {
                var reply = await ping.SendPingAsync(host, 1000, payload, opts).ConfigureAwait(false);
                samples.Add(reply.Status == IPStatus.Success ? reply.RoundtripTime : double.NaN);
            }
            catch { samples.Add(double.NaN); }
            if (i + 1 < count)
            {
                try { await Task.Delay(intervalMs, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }
        return new Run(NetParsing.PingStats(sent, samples), samples);
    }
}
