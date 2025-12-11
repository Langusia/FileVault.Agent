using FileVault.Agent.Node.Protos;
using FileVault.Test.Api.Configuration;
using Grpc.Net.Client;

var builder = WebApplication.CreateBuilder(args);

// Configure upload options
builder.Services.Configure<UploadOptions>(
    builder.Configuration.GetSection(UploadOptions.SectionName));

// Add services to the container
builder.Services.AddControllers();

// Configure Swagger/OpenAPI
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new Microsoft.OpenApi.Models.OpenApiInfo
    {
        Title = "FileVault Test API",
        Version = "v1",
        Description = "REST API for testing FileVault Storage Node Agent via gRPC"
    });

    // Enable file upload in Swagger
    c.OperationFilter<SwaggerFileOperationFilter>();
});

// Configure gRPC client
var grpcAddress = builder.Configuration["GrpcClient:Address"] ?? "http://localhost:5000";
builder.Services.AddSingleton(services =>
{
    var channel = GrpcChannel.ForAddress(grpcAddress, new GrpcChannelOptions
    {
        MaxReceiveMessageSize = null, // Unlimited
        MaxSendMessageSize = null
    });
    return new FileVaultNode.FileVaultNodeClient(channel);
});

// Configure CORS
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
});

var app = builder.Build();

// Configure the HTTP request pipeline
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(c =>
    {
        c.SwaggerEndpoint("/swagger/v1/swagger.json", "FileVault Test API v1");
        c.RoutePrefix = string.Empty; // Serve Swagger UI at root
    });
}

app.UseCors();
app.UseAuthorization();
app.MapControllers();

app.Logger.LogInformation("FileVault Test API starting...");
app.Logger.LogInformation("gRPC Node Agent address: {GrpcAddress}", grpcAddress);
app.Logger.LogInformation("Swagger UI available at: http://localhost:5001");

app.Run();

// Swagger file upload filter
public class SwaggerFileOperationFilter : Swashbuckle.AspNetCore.SwaggerGen.IOperationFilter
{
    public void Apply(
        Microsoft.OpenApi.Models.OpenApiOperation operation,
        Swashbuckle.AspNetCore.SwaggerGen.OperationFilterContext context)
    {
        // Check if this is the streaming upload endpoint
        if (context.ApiDescription.RelativePath?.Contains("upload") == true &&
            context.ApiDescription.HttpMethod?.Equals("POST", StringComparison.OrdinalIgnoreCase) == true)
        {
            operation.RequestBody = new Microsoft.OpenApi.Models.OpenApiRequestBody
            {
                Required = true,
                Content = new Dictionary<string, Microsoft.OpenApi.Models.OpenApiMediaType>
                {
                    ["application/octet-stream"] = new Microsoft.OpenApi.Models.OpenApiMediaType
                    {
                        Schema = new Microsoft.OpenApi.Models.OpenApiSchema
                        {
                            Type = "string",
                            Format = "binary",
                            Description = "Raw file content (streamed)"
                        }
                    }
                }
            };
        }
    }
}
