using LegalAI.Domain.Interfaces;

namespace LegalAI.Domain.DomainModules;

public static class BuiltInDomainModules
{
    public const string Legal = "legal";
    public const string Medical = "medical";
    public const string Finance = "finance";
    public const string Hr = "hr";
    public const string Generic = "generic";

    public static IReadOnlyList<IDomainModule> CreateDefaultSet()
    {
        return
        [
            CreateLegalModule(),
            CreateMedicalModule(),
            CreateFinanceModule(),
            CreateHrModule(),
            CreateGenericModule()
        ];
    }

    private static IDomainModule CreateLegalModule()
    {
        var promptProvider = new StaticPromptTemplateProvider(
            citationFormat: "[Source: file_name, Page X]",
            strictSystemPrompt: """
                You are an evidence-constrained legal assistant.
                Rules:
                1) Use only the provided context.
                2) Do not use external knowledge.
                3) Every claim must include a source citation.
                4) If evidence is insufficient, state it explicitly.
                5) Do not fabricate legal statutes, case numbers, or references.
                6) If sources conflict, present both positions with citations.
                7) Provide analysis only, not verdicts.
                """,
            standardSystemPrompt: """
                You are a legal analysis assistant.
                Prefer context-grounded answers with citations.
                If evidence is insufficient, say so clearly.
                """,
            insufficientEvidenceMessage:
                "لا توجد أدلة كافية في الملفات المفهرسة للإجابة على هذا الاستفسار.\n\nInsufficient evidence found in the indexed corpus.");

        return new ConfigurableDomainModule(
            domainId: Legal,
            displayName: "Legal",
            description: "Case law, regulations, and statutory legal analysis.",
            supportedDocumentTypes: ["pdf", "docx", "txt"],
            pipelineSettings: new DomainPipelineSettings(
                ChunkSize: 512,
                ChunkOverlap: 64,
                EnableNormalization: true,
                EnableMetadataExtraction: true),
            metadataSchema: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["articleReference"] = "string",
                ["caseNumber"] = "string",
                ["courtName"] = "string",
                ["caseDate"] = "string"
            },
            promptTemplates: promptProvider);
    }

    private static IDomainModule CreateMedicalModule()
    {
        var promptProvider = new StaticPromptTemplateProvider(
            citationFormat: "[Source: document, section, page]",
            strictSystemPrompt: """
                You are a clinical knowledge assistant.
                Rules:
                1) Use only provided corpus evidence.
                2) Never invent clinical facts.
                3) Include citations for every recommendation.
                4) If evidence is insufficient, abstain.
                5) Respect privacy and avoid exposing personal data.
                """,
            standardSystemPrompt: """
                You are a healthcare knowledge assistant.
                Ground answers in corpus evidence and provide citations.
                """,
            insufficientEvidenceMessage:
                "Insufficient medical evidence in indexed corpus.");

        return new ConfigurableDomainModule(
            domainId: Medical,
            displayName: "Medical",
            description: "Clinical protocols, guidelines, and healthcare documentation.",
            supportedDocumentTypes: ["pdf", "docx", "txt"],
            pipelineSettings: new DomainPipelineSettings(
                ChunkSize: 384,
                ChunkOverlap: 64,
                EnableNormalization: true,
                EnableMetadataExtraction: true),
            metadataSchema: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["department"] = "string",
                ["guidelineVersion"] = "string",
                ["effectiveDate"] = "string"
            },
            promptTemplates: promptProvider);
    }

    private static IDomainModule CreateFinanceModule()
    {
        var promptProvider = new StaticPromptTemplateProvider(
            citationFormat: "[Source: report, section, page]",
            strictSystemPrompt: """
                You are a finance analysis assistant.
                Rules:
                1) Use only provided financial evidence.
                2) Never invent numbers or ratios.
                3) Cite source sections for all quantitative claims.
                4) State assumptions explicitly.
                """,
            standardSystemPrompt: """
                You are a finance knowledge assistant.
                Provide concise, evidence-backed responses with citations.
                """,
            insufficientEvidenceMessage:
                "Insufficient financial evidence in indexed corpus.");

        return new ConfigurableDomainModule(
            domainId: Finance,
            displayName: "Finance",
            description: "Financial reports, policies, and risk documentation.",
            supportedDocumentTypes: ["pdf", "xlsx", "csv", "txt"],
            pipelineSettings: new DomainPipelineSettings(
                ChunkSize: 420,
                ChunkOverlap: 48,
                EnableNormalization: true,
                EnableMetadataExtraction: true),
            metadataSchema: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["fiscalPeriod"] = "string",
                ["entity"] = "string",
                ["statementType"] = "string"
            },
            promptTemplates: promptProvider);
    }

    private static IDomainModule CreateHrModule()
    {
        var promptProvider = new StaticPromptTemplateProvider(
            citationFormat: "[Source: policy_doc, clause, page]",
            strictSystemPrompt: """
                You are an HR policy assistant.
                Rules:
                1) Use only approved internal policy content.
                2) Provide citations for each policy statement.
                3) If policy evidence is insufficient, abstain.
                4) Avoid personal employee details.
                """,
            standardSystemPrompt: """
                You are an HR knowledge assistant.
                Provide policy-grounded guidance with citations.
                """,
            insufficientEvidenceMessage:
                "Insufficient HR policy evidence in indexed corpus.");

        return new ConfigurableDomainModule(
            domainId: Hr,
            displayName: "HR",
            description: "Employee handbook, policy, and HR compliance content.",
            supportedDocumentTypes: ["pdf", "docx", "txt"],
            pipelineSettings: new DomainPipelineSettings(
                ChunkSize: 400,
                ChunkOverlap: 48,
                EnableNormalization: true,
                EnableMetadataExtraction: true),
            metadataSchema: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["policyName"] = "string",
                ["policyVersion"] = "string",
                ["effectiveDate"] = "string"
            },
            promptTemplates: promptProvider);
    }

    private static IDomainModule CreateGenericModule()
    {
        var promptProvider = new StaticPromptTemplateProvider(
            citationFormat: "[Source: file_name, section, page]",
            strictSystemPrompt: """
                You are an enterprise knowledge assistant.
                Use only provided context and cite every factual claim.
                If evidence is insufficient, explicitly abstain.
                """,
            standardSystemPrompt: """
                You are a knowledge assistant for enterprise documents.
                Prioritize concise, citation-backed responses.
                """,
            insufficientEvidenceMessage:
                "Insufficient evidence in indexed corpus.");

        return new ConfigurableDomainModule(
            domainId: Generic,
            displayName: "Generic",
            description: "General enterprise knowledge base module.",
            supportedDocumentTypes: ["pdf", "docx", "txt", "md"],
            pipelineSettings: new DomainPipelineSettings(
                ChunkSize: 480,
                ChunkOverlap: 48,
                EnableNormalization: true,
                EnableMetadataExtraction: false),
            metadataSchema: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            promptTemplates: promptProvider);
    }

    private sealed class ConfigurableDomainModule : IDomainModule
    {
        public ConfigurableDomainModule(
            string domainId,
            string displayName,
            string description,
            IReadOnlyList<string> supportedDocumentTypes,
            DomainPipelineSettings pipelineSettings,
            IReadOnlyDictionary<string, string> metadataSchema,
            IDomainPromptTemplateProvider promptTemplates)
        {
            DomainId = domainId;
            DisplayName = displayName;
            Description = description;
            SupportedDocumentTypes = supportedDocumentTypes;
            PipelineSettings = pipelineSettings;
            MetadataSchema = metadataSchema;
            PromptTemplates = promptTemplates;
        }

        public string DomainId { get; }
        public string DisplayName { get; }
        public string Description { get; }
        public IReadOnlyList<string> SupportedDocumentTypes { get; }
        public DomainPipelineSettings PipelineSettings { get; }
        public IReadOnlyDictionary<string, string> MetadataSchema { get; }
        public IDomainPromptTemplateProvider PromptTemplates { get; }
    }

    private sealed class StaticPromptTemplateProvider : IDomainPromptTemplateProvider
    {
        private readonly string _strictSystemPrompt;
        private readonly string _standardSystemPrompt;
        private readonly string _insufficientEvidenceMessage;

        public StaticPromptTemplateProvider(
            string citationFormat,
            string strictSystemPrompt,
            string standardSystemPrompt,
            string insufficientEvidenceMessage)
        {
            CitationFormat = citationFormat;
            _strictSystemPrompt = strictSystemPrompt;
            _standardSystemPrompt = standardSystemPrompt;
            _insufficientEvidenceMessage = insufficientEvidenceMessage;
        }

        public string CitationFormat { get; }

        public string GetSystemPrompt(bool strictMode)
        {
            return strictMode ? _strictSystemPrompt : _standardSystemPrompt;
        }

        public string GetInsufficientEvidenceMessage()
        {
            return _insufficientEvidenceMessage;
        }
    }
}
