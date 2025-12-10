using FileVault.Agent.Node.Configuration;
using FileVault.Agent.Node.Interfaces;
using FileVault.Agent.Node.Services;
using FileVault.Agent.Node.Storage;

var builder = WebApplication.CreateBuilder(args);

// Add configuration
builder.Services.Configure<NodeAgentOptions>(
    builder.Configuration.GetSection(NodeAgentOptions.SectionName));

// Validate configuration on startup
builder.Services.AddOptions<NodeAgentOptions>()
    .Bind(builder.Configuration.GetSection(NodeAgentOptions.SectionName))
    .ValidateOnStart();

// Register services
builder.Services.AddSingleton<IPathBuilder, PathBuilder>();
builder.Services.AddSingleton<IFileStorage, LocalFileStorage>();

// Add gRPC
builder.Services.AddGrpc(options =>
{
    options.MaxReceiveMessageSize = null; // Unlimited for streaming
    options.MaxSendMessageSize = null;
});

// Configure Kestrel
builder.WebHost.ConfigureKestrel(options =>
{
    // Configure gRPC endpoint
    options.ListenAnyIP(5000, listenOptions =>
    {
        listenOptions.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http2;
    });
});

var app = builder.Build();

// Validate configuration on startup
var nodeOptions = app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<NodeAgentOptions>>().Value;
try
{
    nodeOptions.Validate();
    app.Logger.LogInformation(
        "Node Agent initialized: NodeId={NodeId}, NodeName={NodeName}, BasePath={BasePath}",
        nodeOptions.NodeId, nodeOptions.NodeName, nodeOptions.BasePath);
}
catch (Exception ex)
{
    app.Logger.LogCritical(ex, "Configuration validation failed");
    throw;
}

// Map gRPC service
app.MapGrpcService<FileVaultNodeService>();

app.MapGet("/", () => "Communication with gRPC endpoints must be made through a gRPC client. To learn how to create a client, visit: https://go.microsoft.com/fwlink/?linkid=2086909");

app.Run();

// Make the implicit Program class public for testing
public partial class Program { }
