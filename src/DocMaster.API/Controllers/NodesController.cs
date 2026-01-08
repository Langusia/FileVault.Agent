using DocMaster.API.Data;
using DocMaster.API.Entities;
using DocMaster.API.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DocMaster.API.Controllers;

[ApiController]
[Route("api/[controller]")]
public class NodesController : ControllerBase
{
    private readonly DocMasterDbContext _dbContext;
    private readonly ILogger<NodesController> _logger;

    public NodesController(DocMasterDbContext dbContext, ILogger<NodesController> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    /// <summary>
    /// Register or update a node
    /// </summary>
    [HttpPost]
    [ProducesResponseType(typeof(NodeDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<NodeDto>> RegisterNode([FromBody] RegisterNodeRequest request)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        var existingNode = await _dbContext.Nodes.FindAsync(request.NodeId);

        if (existingNode != null)
        {
            // Update existing node
            existingNode.GrpcAddress = request.GrpcAddress;
            existingNode.Datacenter = request.Datacenter;
            existingNode.Tier = request.Tier;
            existingNode.UpdatedUtc = DateTime.UtcNow;

            _logger.LogInformation("Updating node {NodeId} with address {GrpcAddress}",
                request.NodeId, request.GrpcAddress);
        }
        else
        {
            // Create new node
            existingNode = new NodeEntity
            {
                NodeId = request.NodeId,
                GrpcAddress = request.GrpcAddress,
                Datacenter = request.Datacenter,
                Tier = request.Tier,
                Status = "Unknown",
                CreatedUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow
            };

            _dbContext.Nodes.Add(existingNode);

            _logger.LogInformation("Registering new node {NodeId} with address {GrpcAddress}",
                request.NodeId, request.GrpcAddress);
        }

        await _dbContext.SaveChangesAsync();

        return Ok(MapToDto(existingNode));
    }

    /// <summary>
    /// Get all nodes
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(NodesListResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<NodesListResponse>> GetNodes()
    {
        var nodes = await _dbContext.Nodes
            .OrderByDescending(n => n.UpdatedUtc)
            .ToListAsync();

        var response = new NodesListResponse
        {
            Nodes = nodes.Select(MapToDto).ToList(),
            TotalCount = nodes.Count
        };

        return Ok(response);
    }

    /// <summary>
    /// Get a specific node by ID
    /// </summary>
    [HttpGet("{nodeId}")]
    [ProducesResponseType(typeof(NodeDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<NodeDto>> GetNode(string nodeId)
    {
        var node = await _dbContext.Nodes.FindAsync(nodeId);

        if (node == null)
        {
            _logger.LogWarning("Node {NodeId} not found", nodeId);
            return NotFound(new { message = $"Node {nodeId} not found" });
        }

        return Ok(MapToDto(node));
    }

    /// <summary>
    /// Delete a node
    /// </summary>
    [HttpDelete("{nodeId}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> DeleteNode(string nodeId)
    {
        var node = await _dbContext.Nodes.FindAsync(nodeId);

        if (node == null)
        {
            _logger.LogWarning("Node {NodeId} not found for deletion", nodeId);
            return NotFound(new { message = $"Node {nodeId} not found" });
        }

        _dbContext.Nodes.Remove(node);
        await _dbContext.SaveChangesAsync();

        _logger.LogInformation("Deleted node {NodeId}", nodeId);

        return NoContent();
    }

    private static NodeDto MapToDto(NodeEntity entity)
    {
        return new NodeDto
        {
            NodeId = entity.NodeId,
            GrpcAddress = entity.GrpcAddress,
            Datacenter = entity.Datacenter,
            Tier = entity.Tier,
            Status = entity.Status,
            LastHeartbeatUtc = entity.LastHeartbeatUtc,
            FreeBytes = entity.FreeBytes,
            TotalBytes = entity.TotalBytes,
            CpuUsagePercent = entity.CpuUsagePercent,
            WorkingSetBytes = entity.WorkingSetBytes,
            UptimeSeconds = entity.UptimeSeconds,
            ConsecutiveFailures = entity.ConsecutiveFailures,
            CreatedUtc = entity.CreatedUtc,
            UpdatedUtc = entity.UpdatedUtc
        };
    }
}
