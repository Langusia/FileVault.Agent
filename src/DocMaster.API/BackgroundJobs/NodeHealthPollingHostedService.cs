using DocMaster.API.Data;
using FileVault.Agent.Node.Protos;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.EntityFrameworkCore;

namespace DocMaster.API.BackgroundJobs;

/// <summary>
/// Background service that periodically polls all registered nodes for health status
/// </summary>
public class NodeHealthPollingHostedService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<NodeHealthPollingHostedService> _logger;
    private readonly TimeSpan _pollingInterval;
    private readonly int _maxConsecutiveFailuresBeforeOffline;

    public NodeHealthPollingHostedService(
        IServiceProvider serviceProvider,
        ILogger<NodeHealthPollingHostedService> logger,
        IConfiguration configuration)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;

        // Read polling interval from config, default to 10 seconds
        var intervalSeconds = configuration.GetValue<int?>("NodeHealthPolling:IntervalSeconds") ?? 10;
        _pollingInterval = TimeSpan.FromSeconds(intervalSeconds);

        // Max consecutive failures before marking offline, default to 3
        _maxConsecutiveFailuresBeforeOffline =
            configuration.GetValue<int?>("NodeHealthPolling:MaxConsecutiveFailures") ?? 3;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Node Health Polling Service starting with interval {Interval}s, max failures {MaxFailures}",
            _pollingInterval.TotalSeconds, _maxConsecutiveFailuresBeforeOffline);

        // Wait a bit before first poll to allow app to fully start
        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PollAllNodesAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during node health polling cycle");
            }

            await Task.Delay(_pollingInterval, stoppingToken);
        }

        _logger.LogInformation("Node Health Polling Service stopped");
    }

    private async Task PollAllNodesAsync(CancellationToken cancellationToken)
    {
        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<DocMasterDbContext>();

        var nodes = await dbContext.Nodes.ToListAsync(cancellationToken);

        if (nodes.Count == 0)
        {
            _logger.LogDebug("No nodes registered, skipping health poll");
            return;
        }

        _logger.LogDebug("Polling health for {NodeCount} nodes", nodes.Count);

        // Poll nodes in parallel for better performance
        var tasks = nodes.Select(node => PollNodeHealthAsync(node, dbContext, cancellationToken));
        await Task.WhenAll(tasks);

        // Save all changes at once
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task PollNodeHealthAsync(
        Entities.NodeEntity node,
        DocMasterDbContext dbContext,
        CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogDebug("Checking health for node {NodeId} at {Address}", node.NodeId, node.GrpcAddress);

            // Create gRPC channel and client
            using var channel = GrpcChannel.ForAddress(node.GrpcAddress, new GrpcChannelOptions
            {
                // Allow HTTP for local dev
                HttpHandler = new SocketsHttpHandler
                {
                    PooledConnectionIdleTimeout = Timeout.InfiniteTimeSpan,
                    KeepAlivePingDelay = TimeSpan.FromSeconds(60),
                    KeepAlivePingTimeout = TimeSpan.FromSeconds(30),
                    EnableMultipleHttp2Connections = true
                }
            });

            var client = new FileVaultNode.FileVaultNodeClient(channel);

            // Call GetHealth with timeout
            var healthRequest = new HealthRequest();
            var deadline = DateTime.UtcNow.AddSeconds(5);

            var response = await client.GetHealthAsync(
                healthRequest,
                deadline: deadline,
                cancellationToken: cancellationToken);

            // Success - update node status
            node.Status = response.IsAlive ? "Online" : "Offline";
            node.LastHeartbeatUtc = DateTime.UtcNow;
            node.ConsecutiveFailures = 0;
            node.FreeBytes = response.DataPathFreeBytes;
            node.TotalBytes = response.DataPathTotalBytes;
            node.UpdatedUtc = DateTime.UtcNow;

            _logger.LogDebug(
                "Node {NodeId} health check successful: Status={Status}, Free={FreeGB}GB, Total={TotalGB}GB",
                node.NodeId, node.Status,
                response.DataPathFreeBytes / 1_073_741_824.0,
                response.DataPathTotalBytes / 1_073_741_824.0);
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Unavailable ||
                                       ex.StatusCode == StatusCode.DeadlineExceeded)
        {
            // Node unreachable
            HandleNodeFailure(node, $"gRPC error: {ex.StatusCode}");
        }
        catch (HttpRequestException ex)
        {
            // HTTP connection error
            HandleNodeFailure(node, $"HTTP error: {ex.Message}");
        }
        catch (Exception ex)
        {
            // Unexpected error
            _logger.LogError(ex, "Unexpected error polling node {NodeId}", node.NodeId);
            HandleNodeFailure(node, $"Unexpected error: {ex.GetType().Name}");
        }
    }

    private void HandleNodeFailure(Entities.NodeEntity node, string reason)
    {
        node.ConsecutiveFailures++;
        node.UpdatedUtc = DateTime.UtcNow;

        if (node.ConsecutiveFailures >= _maxConsecutiveFailuresBeforeOffline)
        {
            if (node.Status != "Offline")
            {
                _logger.LogWarning(
                    "Node {NodeId} marked as Offline after {Failures} consecutive failures. Reason: {Reason}",
                    node.NodeId, node.ConsecutiveFailures, reason);
                node.Status = "Offline";
            }
        }
        else
        {
            _logger.LogDebug(
                "Node {NodeId} health check failed ({Failures}/{MaxFailures}): {Reason}",
                node.NodeId, node.ConsecutiveFailures, _maxConsecutiveFailuresBeforeOffline, reason);
        }
    }
}
