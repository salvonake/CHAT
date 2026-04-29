using System.Security.Cryptography;
using System.Text;
using Poseidon.Api.Localization;
using Poseidon.Api.Security;
using Poseidon.Api.Services;
using Poseidon.Application;
using Poseidon.Application.Services;
using Poseidon.Domain.DomainModules;
using Poseidon.Domain.Interfaces;
using Poseidon.Infrastructure.Audit;
using Poseidon.Infrastructure.Llm;
using Poseidon.Infrastructure.Storage;
using Poseidon.Infrastructure.Telemetry;
using Poseidon.Infrastructure.VectorStore;
using Poseidon.Ingestion.Chunking;
using Poseidon.Ingestion.Embedding;
using Poseidon.Ingestion.Extractors;
using Poseidon.Retrieval.Lexical;
using Poseidon.Retrieval.Pipeline;
using Poseidon.Retrieval.QueryAnalysis;
using Poseidon.Security.Encryption;
using Poseidon.Security.Configuration;
using Poseidon.Security.Injection;
using Poseidon.Security.Secrets;
using MediatR;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);
var services = builder.Services;
var configuration = builder.Configuration;

var instance = InstanceRuntimeOptions.FromConfiguration(configuration);
services.AddSingleton(instance);

services.AddControllers();
services.AddEndpointsApiExplorer();

SecurityConfigurationValidator.ValidateApi(configuration, builder.Environment.EnvironmentName);
var securityContext = SecurityValidationContext.FromConfiguration(configuration, builder.Environment.EnvironmentName);
var jwtSigningKeys = JwtSigningKeyResolver.Resolve(configuration, securityContext);

services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
	.AddJwtBearer(options =>
	{
		options.RequireHttpsMetadata = !builder.Environment.IsDevelopment();
		options.TokenValidationParameters = new TokenValidationParameters
		{
			ValidateIssuerSigningKey = true,
			IssuerSigningKeys = jwtSigningKeys.AllKeys.Select(k => new SymmetricSecurityKey(Encoding.UTF8.GetBytes(k.Value))),
			ValidateIssuer = true,
			ValidIssuer = configuration["Auth:Jwt:Issuer"] ?? "Poseidon",
			ValidateAudience = true,
			ValidAudience = configuration["Auth:Jwt:Audience"] ?? "Poseidon.Client",
			ValidateLifetime = true,
			ClockSkew = TimeSpan.FromMinutes(1)
		};
	});

services.AddAuthorization(options =>
{
	options.AddPolicy("CanRead", p => p.RequireRole("Viewer", "Analyst", "Admin"));
	options.AddPolicy("CanQuery", p => p.RequireRole("Analyst", "Admin"));
	options.AddPolicy("CanIngest", p => p.RequireRole("Analyst", "Admin"));
});

services.AddSwaggerGen();

services.AddMemoryCache();
services.AddSingleton<IJwtTokenService, JwtTokenService>();
services.AddSingleton<ApiTextLocalizer>();

services.AddMediatR(cfg =>
	cfg.RegisterServicesFromAssemblyContaining<AssemblyMarker>());

ConfigureCoreServices(services, configuration, instance, includeRetrieval: true);
services.AddHostedService<ApiStartupInitializationService>();
services.AddHostedService<CentralManagementHeartbeatService>();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
	app.UseSwagger();
	app.UseSwaggerUI();
}

app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapGet("/", () => Results.Ok(new
{
	Service = "Poseidon.Api",
	instance.InstanceName,
	instance.InstanceId,
	instance.Environment
}));

app.Run();

static void ConfigureCoreServices(
	IServiceCollection services,
	IConfiguration cfg,
	InstanceRuntimeOptions instance,
	bool includeRetrieval)
{
	RegisterDomainModules(services, cfg);

	var encPassphrase = ConfigurationSecretResolver.ResolveRequiredSecret(
		cfg,
		SecurityValidationContext.FromConfiguration(cfg),
		"Security:EncryptionPassphrase",
		"Security:EncryptionPassphraseRef",
		32).Value;
	var encEnabled = cfg.GetValue("Security:EncryptionEnabled", false);
	services.AddSingleton<IEncryptionService>(sp =>
		new AesGcmEncryptionService(
			encPassphrase,
			sp.GetRequiredService<ILogger<AesGcmEncryptionService>>(),
			encEnabled));

	var llmProvider = (cfg["Llm:Provider"] ?? "ollama").ToLowerInvariant();
	if (llmProvider == "ollama")
	{
		services.AddSingleton<ILlmService>(sp =>
		{
			var http = new HttpClient
			{
				BaseAddress = new Uri(cfg["Ollama:Url"] ?? "http://localhost:11434")
			};

			return new OllamaLlmService(
				http,
				cfg["Ollama:Model"] ?? "qwen2.5:14b",
				sp.GetRequiredService<ILogger<OllamaLlmService>>());
		});
	}
	else
	{
		services.AddSingleton<ILlmService>(sp =>
		{
			var modelPath = cfg["Llm:ModelPath"];
			if (string.IsNullOrWhiteSpace(modelPath))
			{
				modelPath = Path.Combine(instance.ModelsDirectory, "model.gguf");
			}

			return new LLamaSharpLlmService(
				modelPath,
				cfg.GetValue("Llm:GpuLayers", -1),
				cfg.GetValue("Llm:ContextSize", 8192),
				sp.GetRequiredService<ILogger<LLamaSharpLlmService>>());
		});
	}

	var embProvider = (cfg["Embedding:Provider"] ?? "ollama").ToLowerInvariant();
	var embDimension = cfg.GetValue("Embedding:Dimension", 768);
	if (embProvider == "ollama")
	{
		services.AddSingleton<IEmbeddingService>(sp =>
		{
			var http = new HttpClient
			{
				BaseAddress = new Uri(cfg["Ollama:Url"] ?? "http://localhost:11434")
			};

			return new OllamaEmbeddingService(
				http,
				cfg["Embedding:Model"] ?? "nomic-embed-text",
				sp.GetRequiredService<ILogger<OllamaEmbeddingService>>(),
				embDimension);
		});
	}
	else
	{
		services.AddSingleton<IEmbeddingService>(sp =>
		{
			var onnxPath = cfg["Embedding:OnnxModelPath"];
			if (string.IsNullOrWhiteSpace(onnxPath))
			{
				onnxPath = Path.Combine(instance.ModelsDirectory, "embedding_model.onnx");
			}

			var vocabPath = cfg["Embedding:VocabPath"];
			if (string.IsNullOrWhiteSpace(vocabPath))
			{
				vocabPath = null;
			}

			return new OnnxArabicEmbeddingService(
				onnxPath,
				sp.GetRequiredService<ILogger<OnnxArabicEmbeddingService>>(),
				vocabPath,
				embDimension);
		});
	}

	var vectorProvider = (cfg["VectorStore:Provider"] ?? "qdrant").ToLowerInvariant();
	if (vectorProvider == "embedded")
	{
		services.AddSingleton<IVectorStore>(sp =>
			new EmbeddedVectorStore(
				instance.VectorDbPath,
				instance.HnswIndexPath,
				sp.GetRequiredService<ILogger<EmbeddedVectorStore>>()));
	}
	else
	{
		var collectionName = cfg["Qdrant:CollectionName"];
		if (string.IsNullOrWhiteSpace(collectionName))
		{
			collectionName = $"poseidon_{instance.SanitizedInstanceId}_chunks";
		}

		services.AddSingleton<IVectorStore>(sp =>
			new QdrantVectorStore(
				cfg["Qdrant:Host"] ?? "localhost",
				cfg.GetValue("Qdrant:Port", 6334),
				collectionName,
				embDimension,
				sp.GetRequiredService<ILogger<QdrantVectorStore>>()));
	}

	services.AddSingleton<IDocumentStore>(sp =>
		new SqliteDocumentStore(
			instance.DocumentDbPath,
			sp.GetRequiredService<ILogger<SqliteDocumentStore>>()));

	services.AddSingleton<IDatasetStore>(sp =>
		new SqliteDatasetStore(
			instance.DocumentDbPath,
			sp.GetRequiredService<ILogger<SqliteDatasetStore>>()));

	services.AddSingleton<IUserStore>(sp =>
		new SqliteUserStore(
			instance.IdentityDbPath,
			sp.GetRequiredService<ILogger<SqliteUserStore>>()));

	services.AddSingleton<IUserDomainGrantStore>(sp =>
		new SqliteUserDomainGrantStore(
			instance.IdentityDbPath,
			sp.GetRequiredService<ILogger<SqliteUserDomainGrantStore>>()));

	services.AddSingleton<IChatStore>(sp =>
		new SqliteChatStore(
			instance.ChatDbPath,
			sp.GetRequiredService<ILogger<SqliteChatStore>>()));

	services.AddSingleton<IIngestionJobStore>(sp =>
		new SqliteIngestionJobStore(
			instance.IngestionJobsDbPath,
			sp.GetRequiredService<ILogger<SqliteIngestionJobStore>>()));

	services.AddSingleton<IAuditService>(sp =>
		new SqliteAuditService(
			instance.AuditDbPath,
			sp.GetRequiredService<IEncryptionService>(),
			sp.GetRequiredService<ILogger<SqliteAuditService>>()));

	services.AddSingleton<IMetricsCollector, InMemoryMetricsCollector>();
	services.AddSingleton<IPromptTemplateEngine, PromptTemplateEngine>();
	services.AddSingleton<IPdfExtractor, PdfPigExtractor>();
	services.AddSingleton<IDomainChunker, LegalDocumentChunker>();
	services.AddSingleton<IDocumentChunker>(sp => sp.GetRequiredService<IDomainChunker>());
	services.AddSingleton<IInjectionDetector, PromptInjectionDetector>();

	if (includeRetrieval)
	{
		services.AddSingleton<BM25Index>();
		services.AddSingleton<IDomainQueryAnalyzer, LegalQueryAnalyzer>();
		services.AddSingleton<IRetrievalPipeline, LegalRetrievalPipeline>();
	}
}

static void RegisterDomainModules(IServiceCollection services, IConfiguration cfg)
{
	foreach (var module in BuiltInDomainModules.CreateDefaultSet())
	{
		services.AddSingleton<IDomainModule>(module);
	}

	services.AddSingleton<IDomainModuleRegistry>(sp =>
	{
		var activeDomain = cfg["Domain:ActiveModule"] ?? BuiltInDomainModules.Legal;
		return new InMemoryDomainModuleRegistry(sp.GetServices<IDomainModule>(), activeDomain);
	});
}

internal sealed class ApiStartupInitializationService : IHostedService
{
	private readonly IVectorStore _vectorStore;
	private readonly IAuditService _auditService;
	private readonly InstanceRuntimeOptions _instance;
	private readonly ILogger<ApiStartupInitializationService> _logger;

	public ApiStartupInitializationService(
		IVectorStore vectorStore,
		IAuditService auditService,
		InstanceRuntimeOptions instance,
		ILogger<ApiStartupInitializationService> logger)
	{
		_vectorStore = vectorStore;
		_auditService = auditService;
		_instance = instance;
		_logger = logger;
	}

	public async Task StartAsync(CancellationToken cancellationToken)
	{
		await _vectorStore.InitializeAsync(cancellationToken);
		await _auditService.LogAsync(
			"API_STARTED",
			$"Instance {_instance.InstanceName} ({_instance.InstanceId}) initialized",
			"system",
			cancellationToken);

		_logger.LogInformation(
			"API startup initialization complete for {InstanceName} ({InstanceId})",
			_instance.InstanceName,
			_instance.InstanceId);
	}

	public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

internal sealed class InstanceRuntimeOptions
{
	public required string InstanceName { get; init; }
	public required string InstanceId { get; init; }
	public required string SanitizedInstanceId { get; init; }
	public required string Environment { get; init; }
	public required string RootDirectory { get; init; }
	public required string DataDirectory { get; init; }
	public required string LogsDirectory { get; init; }
	public required string ModelsDirectory { get; init; }
	public required string WatchDirectory { get; init; }
	public required string QuarantineDirectory { get; init; }
	public required string DocumentDbPath { get; init; }
	public required string IdentityDbPath { get; init; }
	public required string ChatDbPath { get; init; }
	public required string IngestionJobsDbPath { get; init; }
	public required string AuditDbPath { get; init; }
	public required string VectorDbPath { get; init; }
	public required string HnswIndexPath { get; init; }

	public static InstanceRuntimeOptions FromConfiguration(IConfiguration cfg)
	{
		var instanceName = cfg["Instance:Name"];
		if (string.IsNullOrWhiteSpace(instanceName))
		{
			instanceName = "Poseidon";
		}

		var instanceId = cfg["Instance:Id"];
		if (string.IsNullOrWhiteSpace(instanceId))
		{
			var seed = $"{System.Environment.MachineName}|{instanceName}|{cfg["Instance:Environment"] ?? "Production"}";
			var hash = SHA256.HashData(Encoding.UTF8.GetBytes(seed));
			instanceId = Convert.ToHexString(hash)[..12].ToLowerInvariant();
		}

		var sanitizedName = Sanitize(instanceName);
		var sanitizedId = Sanitize(instanceId);
		var environment = cfg["Instance:Environment"] ?? "Production";

		var configuredRoot = cfg["Storage:RootPath"];
		var rootBase = string.IsNullOrWhiteSpace(configuredRoot)
			? Path.Combine(
				System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
				"Poseidon",
				"Instances")
			: configuredRoot;

		var rootDirectory = Path.Combine(rootBase, $"{sanitizedName}_{sanitizedId}");
		var dataDirectory = Path.Combine(rootDirectory, "Data");
		var logsDirectory = Path.Combine(rootDirectory, "Logs");
		var modelsDirectory = Path.Combine(rootDirectory, "Models");
		var quarantineDirectory = Path.Combine(rootDirectory, "Quarantine");

		var watchDirectory = cfg["Data:PdfWatchDirectory"];
		if (string.IsNullOrWhiteSpace(watchDirectory))
		{
			watchDirectory = Path.Combine(rootDirectory, "Pdfs");
		}

		Directory.CreateDirectory(rootDirectory);
		Directory.CreateDirectory(dataDirectory);
		Directory.CreateDirectory(logsDirectory);
		Directory.CreateDirectory(modelsDirectory);
		Directory.CreateDirectory(quarantineDirectory);
		Directory.CreateDirectory(watchDirectory);

		return new InstanceRuntimeOptions
		{
			InstanceName = instanceName,
			InstanceId = instanceId,
			SanitizedInstanceId = sanitizedId,
			Environment = environment,
			RootDirectory = rootDirectory,
			DataDirectory = dataDirectory,
			LogsDirectory = logsDirectory,
			ModelsDirectory = modelsDirectory,
			WatchDirectory = watchDirectory,
			QuarantineDirectory = quarantineDirectory,
			DocumentDbPath = Path.Combine(dataDirectory, "documents.db"),
			IdentityDbPath = Path.Combine(dataDirectory, "identity.db"),
			ChatDbPath = Path.Combine(dataDirectory, "chat.db"),
			IngestionJobsDbPath = Path.Combine(dataDirectory, "ingestion-jobs.db"),
			AuditDbPath = Path.Combine(dataDirectory, "audit.db"),
			VectorDbPath = Path.Combine(dataDirectory, "vectors.db"),
			HnswIndexPath = Path.Combine(dataDirectory, "vectors.hnsw")
		};
	}

	private static string Sanitize(string value)
	{
		var sb = new StringBuilder(value.Length);
		foreach (var ch in value)
		{
			if (char.IsLetterOrDigit(ch))
			{
				sb.Append(char.ToLowerInvariant(ch));
			}
			else if (ch is '-' or '_' or '.')
			{
				sb.Append('_');
			}
		}

		if (sb.Length == 0)
		{
			return "default";
		}

		return sb.ToString();
	}
}

