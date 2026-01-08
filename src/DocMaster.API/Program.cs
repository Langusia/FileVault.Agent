using DocMaster.API.BackgroundJobs;
using DocMaster.API.Data;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new Microsoft.OpenApi.Models.OpenApiInfo
    {
        Title = "DocMaster API",
        Version = "v1",
        Description = "FileVault cluster management and metadata API"
    });
});

// Configure Database
var connectionString = builder.Configuration.GetConnectionString("DocMasterDb");

if (string.IsNullOrEmpty(connectionString))
{
    throw new InvalidOperationException(
        "Database connection string 'DocMasterDb' is missing. " +
        "Please configure it in appsettings.json or environment variables.");
}

builder.Services.AddDbContext<DocMasterDbContext>(options =>
{
    // Determine database provider based on connection string
    if (connectionString.Contains("Host=") || connectionString.Contains("Server=") && connectionString.Contains("Port="))
    {
        // PostgreSQL
        options.UseNpgsql(connectionString, npgsqlOptions =>
        {
            npgsqlOptions.EnableRetryOnFailure(
                maxRetryCount: 5,
                maxRetryDelay: TimeSpan.FromSeconds(10),
                errorCodesToAdd: null);
        });
        builder.Logging.AddConsole().SetMinimumLevel(LogLevel.Information);
    }
    else
    {
        throw new InvalidOperationException(
            "Only PostgreSQL is supported. Connection string must contain 'Host=' or 'Server=' with 'Port='");
    }

    // Enable sensitive data logging in development
    if (builder.Environment.IsDevelopment())
    {
        options.EnableSensitiveDataLogging();
        options.EnableDetailedErrors();
    }
});

// Register background services
builder.Services.AddHostedService<NodeHealthPollingHostedService>();

// Configure logging
builder.Logging.AddConsole();
builder.Logging.AddDebug();

var app = builder.Build();

// Auto-migrate database on startup
await MigrateDatabaseAsync(app.Services);

// Configure the HTTP request pipeline
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(options =>
    {
        options.SwaggerEndpoint("/swagger/v1/swagger.json", "DocMaster API v1");
        options.RoutePrefix = string.Empty; // Serve Swagger UI at root
    });
}

app.UseHttpsRedirection();
app.UseAuthorization();
app.MapControllers();

app.Logger.LogInformation("DocMaster.API starting on {Environment}", app.Environment.EnvironmentName);
app.Logger.LogInformation("Swagger UI available at: {Url}", app.Environment.IsDevelopment() ? "http://localhost:5000" : "disabled");

app.Run();

// Method to auto-migrate database on startup
static async Task MigrateDatabaseAsync(IServiceProvider serviceProvider)
{
    using var scope = serviceProvider.CreateScope();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    var dbContext = scope.ServiceProvider.GetRequiredService<DocMasterDbContext>();

    try
    {
        logger.LogInformation("Applying database migrations...");

        // Check if database exists and create/migrate
        var pendingMigrations = await dbContext.Database.GetPendingMigrationsAsync();
        var pendingCount = pendingMigrations.Count();

        if (pendingCount > 0)
        {
            logger.LogInformation("Found {Count} pending migrations. Applying...", pendingCount);
            await dbContext.Database.MigrateAsync();
            logger.LogInformation("Database migrations applied successfully");
        }
        else
        {
            logger.LogInformation("Database is up to date, no pending migrations");
        }

        // Verify connection
        var canConnect = await dbContext.Database.CanConnectAsync();
        if (canConnect)
        {
            logger.LogInformation("Database connection verified successfully");
        }
        else
        {
            logger.LogWarning("Database connection check failed");
        }
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "An error occurred while migrating the database");
        logger.LogError("Connection string (masked): {ConnectionString}",
            MaskConnectionString(dbContext.Database.GetConnectionString() ?? "null"));
        throw;
    }
}

static string MaskConnectionString(string connectionString)
{
    if (string.IsNullOrEmpty(connectionString))
        return connectionString;

    // Mask password in connection string for logging
    var parts = connectionString.Split(';');
    var masked = parts.Select(part =>
    {
        if (part.Trim().StartsWith("Password=", StringComparison.OrdinalIgnoreCase))
            return "Password=***";
        return part;
    });
    return string.Join(';', masked);
}

// Make Program class accessible for testing
public partial class Program { }
