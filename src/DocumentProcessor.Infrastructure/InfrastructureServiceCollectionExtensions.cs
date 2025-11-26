using DocumentProcessor.Core.Interfaces;
using DocumentProcessor.Infrastructure.AI;
using DocumentProcessor.Infrastructure.BackgroundTasks;
using DocumentProcessor.Infrastructure.Data;
using DocumentProcessor.Infrastructure.Providers;
using DocumentProcessor.Infrastructure.Repositories;
using DocumentProcessor.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DocumentProcessor.Infrastructure;

public static class InfrastructureServiceCollectionExtensions
{
    public static IServiceCollection AddInfrastructureServices(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // Add Entity Framework - Always use PostgreSQL (RDS)
        // Default: Use AWS Secrets Manager
        // Fallback: Use local connection string from configuration
        string connectionString;

        try
        {
            // Primary: Build connection string from AWS Secrets Manager
            connectionString = BuildConnectionStringFromSecretsManager().GetAwaiter().GetResult();
        }
        catch
        {
            // Fallback: Use local connection string if Secrets Manager is unavailable
            var localConnectionString = configuration.GetConnectionString("DefaultConnection");
            if (string.IsNullOrEmpty(localConnectionString))
            {
                throw new InvalidOperationException("Unable to retrieve connection string from AWS Secrets Manager and no local connection string is configured.");
            }
            connectionString = localConnectionString;
        }

        services.AddDbContext<ApplicationDbContext>(options => options.UseNpgsql(connectionString));

        // Register repositories
        services.AddScoped<IDocumentRepository, DocumentRepository>();

        // Register Unit of Work
        services.AddScoped<IUnitOfWork, UnitOfWork>();

        // Register document source providers
        services.AddSingleton<LocalFileSystemProvider>();
        services.AddSingleton<IDocumentSourceFactory, DocumentSourceFactory>();
        services.AddScoped<IDocumentSourceProvider>(provider =>
        {
            var factory = provider.GetRequiredService<IDocumentSourceFactory>();
            var sourceType = configuration.GetValue<string>("DocumentStorage:Provider") ?? "LocalFileSystem";
            return factory.CreateProvider(sourceType);
        });

        // Register AI processing services
        // Change to Scoped to support scoped dependencies
        services.AddScoped<IAIProcessorFactory, AIProcessorFactory>();
        
        // Register DocumentContentExtractor
        services.AddScoped<DocumentContentExtractor>();

        // Register Bedrock configuration
        var bedrockSection = configuration.GetSection("Bedrock");
        services.Configure<BedrockOptions>(options =>
        {
            bedrockSection.Bind(options);
        });
            
        // Register background task services
        services.AddSingleton<IBackgroundTaskQueue, BackgroundTaskQueue>();
            
        // Register hosted services
        var maxConcurrency = configuration.GetValue<int>("BackgroundTasks:MaxConcurrency", 3);

        // Register the DocumentProcessingHostedService
        services.AddHostedService(provider =>
        {
            var logger = provider.GetRequiredService<ILogger<DocumentProcessingHostedService>>();
            var queue = provider.GetRequiredService<IBackgroundTaskQueue>();
            logger.LogInformation("Creating DocumentProcessingHostedService with max concurrency: {MaxConcurrency}", maxConcurrency);
            return new DocumentProcessingHostedService(queue, logger, maxConcurrency);
        });

        // Note: IDocumentProcessingService is registered in the Application layer

        return services;
    }

    public static IServiceCollection AddInfrastructureHealthChecks(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddHealthChecks()
            .AddDbContextCheck<ApplicationDbContext>("database")
            .AddCheck("document-storage", () =>
            {
                // Simple health check for document storage
                var provider = configuration.GetValue<string>("DocumentStorage:Provider");
                if (string.IsNullOrEmpty(provider))
                {
                    return HealthCheckResult.Unhealthy("No document storage provider configured");
                }
                return HealthCheckResult.Healthy($"Document storage provider: {provider}");
            });

        return services;
    }

    public static async Task EnsureDatabaseAsync(this IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<ApplicationDbContext>>();
        var hostEnvironment = scope.ServiceProvider.GetRequiredService<IHostEnvironment>();

        try
        {
            logger.LogInformation("Ensuring database exists...");

            // Always drop and recreate the database to ensure schema is up to date
            logger.LogWarning("Dropping and recreating database to ensure schema is current");
            // await context.Database.EnsureDeletedAsync();
            logger.LogInformation("Database dropped successfully");

            // Create database from model without running migrations
            // This will create the database with all tables, indexes, and relationships
            // based on the current DbContext model
            var created = await context.Database.EnsureCreatedAsync();

            if (created)
            {
                logger.LogInformation("Database created successfully from model");

                // Create database view for document summaries
                logger.LogInformation("Creating database view: vw_documentsummary");
                await context.Database.ExecuteSqlRawAsync(@"
                    CREATE VIEW dps_dbo.vw_documentsummary AS
                    SELECT
                        documenttypename,
                        status,
                        COUNT(*) AS documentcount,
                        AVG(aws_sqlserver_ext.datediff('second', uploadedat::TIMESTAMP, COALESCE(processedat, timezone('UTC', CURRENT_TIMESTAMP(6)))::TIMESTAMP)) AS avgprocessingtimeseconds,
                        MIN(uploadedat) AS firstuploadedat,
                        MAX(uploadedat) AS lastuploadedat
                    FROM dps_dbo.documents
                    WHERE isdeleted = 0
                    GROUP BY documenttypename, status
                ");
                logger.LogInformation("Created view: vw_documentsummary");

                // Create function for getting recent documents
                logger.LogInformation("Creating function: sp_getrecentdocuments");
                await context.Database.ExecuteSqlRawAsync(@"
                    CREATE OR REPLACE FUNCTION dps_dbo.sp_getrecentdocuments(
                        days INTEGER DEFAULT 7,
                        status_param INTEGER DEFAULT NULL,
                        documenttypename_param VARCHAR(200) DEFAULT NULL
                    )
                    RETURNS TABLE (
                        id UUID,
                        filename VARCHAR(500),
                        fileextension VARCHAR(50),
                        storagepath VARCHAR(1000),
                        filesize BIGINT,
                        documenttypename VARCHAR(200),
                        documenttypecategory VARCHAR(100),
                        status INTEGER,
                        processingstatus INTEGER,
                        summary TEXT,
                        uploadedat TIMESTAMP,
                        processedat TIMESTAMP,
                        processingstartedat TIMESTAMP,
                        processingcompletedat TIMESTAMP
                    )
                    LANGUAGE plpgsql
                    AS $
                    BEGIN
                        RETURN QUERY
                        SELECT
                            d.id,
                            d.filename,
                            d.fileextension,
                            d.storagepath,
                            d.filesize,
                            d.documenttypename,
                            d.documenttypecategory,
                            d.status,
                            d.processingstatus,
                            d.summary,
                            d.uploadedat,
                            d.processedat,
                            d.processingstartedat,
                            d.processingcompletedat
                        FROM dps_dbo.documents d
                        WHERE d.isdeleted = 0
                            AND d.uploadedat >= (timezone('UTC', CURRENT_TIMESTAMP) - make_interval(days => days))
                            AND (status_param IS NULL OR d.status = status_param)
                            AND (documenttypename_param IS NULL OR d.documenttypename = documenttypename_param)
                        ORDER BY d.uploadedat DESC;
                    END;
                    $
                ");
                logger.LogInformation("Created function: sp_getrecentdocuments");
            }
            else
            {
                logger.LogInformation("Database already exists");
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "An error occurred while ensuring database exists");
            throw;
        }
    }

    /// <summary>
    /// Builds a PostgreSQL connection string from AWS Secrets Manager
    /// </summary>
    /// 
    private static async Task<string> BuildConnectionStringFromSecretsManager()
    {
        var secretsService = new SecretsManagerService();
        string secretJson;

        try
        {
            // Use the target PostgreSQL database secret
            secretJson = await secretsService.GetSecretAsync("arn:aws:secretsmanager:us-east-1:050752607737:secret:atx-db-modernization-atx-db-modernization-1-target-cluvvE");
            if (!string.IsNullOrWhiteSpace(secretJson))
            {
                var username = secretsService.GetFieldFromSecret(secretJson, "username");
                var password = secretsService.GetFieldFromSecret(secretJson, "password");
                var host = secretsService.GetFieldFromSecret(secretJson, "host");
                var port = secretsService.GetFieldFromSecret(secretJson, "port");
                var dbname = "postgres";

                return $"Host={host};Port={port};Database={dbname};Username={username};Password={password};SSL Mode=Require;Trust Server Certificate=true";
            }
        }
        catch (Exception)
        {
        }

        throw new InvalidOperationException("Failed to retrieve database credentials from Secrets Manager.");
    }
}