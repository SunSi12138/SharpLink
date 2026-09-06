using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SharpLink.Abstractions;
using SharpLink.Client;
using SharpLink.Runtime;
using SharpLink.Sdk;
using SharpLink.Server;

namespace SharpLink.ChaosTests;

internal sealed class ChaosServer(SharpLinkServer server, Task runTask, int port)
{
    internal int Port { get; } = port;

    internal static Task<ChaosServer> StartAsync(
        ChaosTransport transport,
        string sharedMemoryName,
        int port,
        ILoggerFactory loggerFactory)
    {
        var builder = SharpLinkServerBuilder.Create()
            .UseLoggerFactory(loggerFactory)
            .UseHeartbeat(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5));
        if (transport == ChaosTransport.SharedMemory)
            builder.UseSharedMemory(sharedMemoryName);
        else
            builder.UseTcp(port, IPAddress.Loopback.ToString());
        var boundPort = transport == ChaosTransport.Tcp
            ? ((IPEndPoint)builder.Transport!.LocalEndPoint!).Port
            : 0;
        var server = (SharpLinkServer)builder.Build();
        var runTask = server.RunAsync().AsTask();
        return Task.FromResult(new ChaosServer(server, runTask, boundPort));
    }

    internal static async Task<ChaosServer> StartWithRetryAsync(
        ChaosTransport transport,
        string sharedMemoryName,
        int port,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        Exception? lastException = null;
        for (var attempt = 0; attempt < 100; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return await StartAsync(transport, sharedMemoryName, port, loggerFactory).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is SocketException or IOException)
            {
                lastException = exception;
                await Task.Delay(20, cancellationToken).ConfigureAwait(false);
            }
        }
        throw new InvalidOperationException("TCP listener did not become reusable after rolling restart.", lastException);
    }

    internal async Task<ChaosServerStopObservation> StopAsync(string reason)
    {
        await server.StopAsync(TimeSpan.FromMilliseconds(100)).ConfigureAwait(false);
        await runTask.WaitAsync(TimeSpan.FromSeconds(6)).ConfigureAwait(false);
        return new ChaosServerStopObservation(
            DateTimeOffset.UtcNow,
            reason,
            server.ActiveCallCountForDiagnostics,
            server.LastStopDiagnostics);
    }
}

internal sealed record ChaosServerStopObservation(
    DateTimeOffset TimestampUtc,
    string Reason,
    int ActiveCallsAfterStop,
    ServerStopDiagnosticSnapshot? GraceTimeoutSnapshot);
