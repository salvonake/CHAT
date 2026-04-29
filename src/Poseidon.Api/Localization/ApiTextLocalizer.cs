using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace Poseidon.Api.Localization;

public sealed class ApiTextLocalizer
{
    private readonly string _defaultLanguage;
    private readonly HashSet<string> _supportedLanguages;

    private static readonly Dictionary<string, (string Fr, string En, string Ar)> Messages =
        new(StringComparer.Ordinal)
        {
            ["QuestionRequired"] = (
                "La question est obligatoire.",
                "Question is required.",
                "السوال مطلوب."),
            ["UserIdentityNotFound"] = (
                "Identite utilisateur introuvable.",
                "User identity not found.",
                "تعذر العثور على هوية المستخدم."),
            ["UsernamePasswordRequired"] = (
                "Le nom d'utilisateur et le mot de passe sont obligatoires.",
                "Username and password are required.",
                "اسم المستخدم وكلمة المرور مطلوبان."),
            ["BootstrapRequiresNoUsers"] = (
                "Le bootstrap est autorise uniquement lorsqu'aucun utilisateur n'existe.",
                "Bootstrap is only allowed when no users exist.",
                "التهيئة الاولية مسموحة فقط عند عدم وجود مستخدمين."),
            ["AdminUserCreated"] = (
                "Utilisateur administrateur cree.",
                "Admin user created.",
                "تم إنشاء مستخدم مدير."),
            ["InvalidCredentials"] = (
                "Identifiants invalides.",
                "Invalid credentials.",
                "بيانات الاعتماد غير صالحة."),
            ["InvalidRole"] = (
                "Role invalide. Utilisez Admin, Analyst ou Viewer.",
                "Invalid role. Use Admin, Analyst, or Viewer.",
                "دور غير صالح. استخدم Admin او Analyst او Viewer."),
            ["UsernameAlreadyExists"] = (
                "Le nom d'utilisateur existe deja.",
                "Username already exists.",
                "اسم المستخدم موجود بالفعل."),
            ["UserNotFound"] = (
                "Utilisateur introuvable.",
                "User not found.",
                "المستخدم غير موجود."),
            ["UserStatusUpdated"] = (
                "Statut utilisateur mis a jour.",
                "User status updated.",
                "تم تحديث حالة المستخدم."),
            ["DomainIdRequired"] = (
                "DomainId est obligatoire.",
                "DomainId is required.",
                "معرف النطاق مطلوب."),
            ["DomainAccessRevoked"] = (
                "Acces au domaine revoque.",
                "Domain access revoked.",
                "تم سحب صلاحية الوصول للنطاق."),
            ["TitleRequired"] = (
                "Le titre est obligatoire.",
                "Title is required.",
                "العنوان مطلوب."),
            ["ChatSessionNotFound"] = (
                "Session de discussion introuvable.",
                "Chat session not found.",
                "جلسة الدردشة غير موجودة."),
            ["ChatRenamed"] = (
                "Discussion renommee.",
                "Chat renamed.",
                "تمت إعادة تسمية الدردشة."),
            ["ChatArchiveStateUpdated"] = (
                "Etat d'archivage de la discussion mis a jour.",
                "Chat archive state updated.",
                "تم تحديث حالة ارشفة الدردشة."),
            ["MissingUserIdentity"] = (
                "Identite utilisateur manquante.",
                "Missing user identity.",
                "هوية المستخدم مفقودة."),
            ["DomainAndNameRequired"] = (
                "DomainId et Name sont obligatoires.",
                "DomainId and Name are required.",
                "DomainId و Name مطلوبان."),
            ["UnknownDomain"] = (
                "Domaine inconnu '{0}'.",
                "Unknown domain '{0}'.",
                "نطاق غير معروف '{0}'."),
            ["DatasetNameExists"] = (
                "Un dataset avec ce nom existe deja dans ce domaine.",
                "A dataset with this name already exists in the domain.",
                "توجد مجموعة بيانات بهذا الاسم بالفعل في النطاق."),
            ["DatasetNotFound"] = (
                "Dataset introuvable.",
                "Dataset not found.",
                "مجموعة البيانات غير موجودة."),
            ["DatasetLifecycleUpdated"] = (
                "Cycle de vie du dataset mis a jour.",
                "Dataset lifecycle updated.",
                "تم تحديث دورة حياة مجموعة البيانات."),
            ["FilePathRequired"] = (
                "Le chemin du fichier est obligatoire.",
                "File path is required.",
                "مسار الملف مطلوب."),
            ["FileNotFound"] = (
                "Fichier introuvable.",
                "File not found.",
                "الملف غير موجود."),
            ["DirectoryPathRequired"] = (
                "Le chemin du dossier est obligatoire.",
                "Directory path is required.",
                "مسار المجلد مطلوب."),
            ["DirectoryNotFound"] = (
                "Dossier introuvable.",
                "Directory not found.",
                "المجلد غير موجود."),
            ["DocumentIndexedSuccessfully"] = (
                "Document indexe avec succes.",
                "Document indexed successfully.",
                "تمت فهرسة المستند بنجاح."),
            ["DocumentsIndexedSummary"] = (
                "{0} document(s) indexe(s) sur {1}.",
                "Indexed {0} of {1} documents.",
                "تمت فهرسة {0} من {1} مستند."),
            ["DatasetArchivedCannotIngest"] = (
                "Le dataset est archive et ne peut pas recevoir de nouvelles ingestions.",
                "Dataset is archived and cannot receive new ingestions.",
                "مجموعة البيانات مؤرشفة ولا يمكنها استقبال عمليات إدخال جديدة."),
            ["DatasetDomainMismatch"] = (
                "Le dataset n'appartient pas au domaine demande.",
                "Dataset does not belong to the requested domain.",
                "مجموعة البيانات لا تنتمي إلى النطاق المطلوب."),
            ["CaseNamespaceMismatch"] = (
                "L'espace de cas ne correspond pas au domaine/scope resolu.",
                "Case namespace does not match the resolved domain/dataset scope.",
                "مساحة الحالة لا تطابق النطاق/المجموعة المحددة."),
            ["HealthStatusHealthy"] = (
                "Sain",
                "Healthy",
                "سليم"),
            ["HealthStatusUnhealthy"] = (
                "Degrade",
                "Unhealthy",
                "غير سليم")
        };

    public ApiTextLocalizer(IConfiguration configuration)
    {
        var configuredDefault =
            Environment.GetEnvironmentVariable("DEFAULT_LANGUAGE")
            ?? configuration["Localization:DefaultLanguage"]
            ?? "fr";

        _defaultLanguage = NormalizeLanguage(configuredDefault);

        var configuredSupported =
            Environment.GetEnvironmentVariable("SUPPORTED_LANGUAGES")
            ?? configuration["Localization:SupportedLanguages"]
            ?? "fr,ar,en";

        _supportedLanguages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var token in configuredSupported.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var normalized = NormalizeLanguage(token);
            if (!string.IsNullOrWhiteSpace(normalized))
            {
                _supportedLanguages.Add(normalized);
            }
        }

        if (_supportedLanguages.Count == 0)
        {
            _supportedLanguages.Add("fr");
            _supportedLanguages.Add("ar");
            _supportedLanguages.Add("en");
        }
    }

    public string ResolveLanguage(HttpContext? httpContext, string? explicitLanguage = null)
    {
        var candidates = new List<string>();

        if (!string.IsNullOrWhiteSpace(explicitLanguage))
        {
            candidates.Add(explicitLanguage);
        }

        if (httpContext?.Request is not null)
        {
            var queryLanguage = httpContext.Request.Query["lang"].ToString();
            if (!string.IsNullOrWhiteSpace(queryLanguage))
            {
                candidates.Add(queryLanguage);
            }

            var acceptLanguage = httpContext.Request.Headers.AcceptLanguage.ToString();
            if (!string.IsNullOrWhiteSpace(acceptLanguage))
            {
                foreach (var token in acceptLanguage.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    var languagePart = token.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)[0];
                    if (!string.IsNullOrWhiteSpace(languagePart))
                    {
                        candidates.Add(languagePart);
                    }
                }
            }
        }

        foreach (var candidate in candidates)
        {
            var normalized = NormalizeLanguage(candidate);
            if (_supportedLanguages.Contains(normalized))
            {
                return normalized;
            }
        }

        return _supportedLanguages.Contains(_defaultLanguage) ? _defaultLanguage : "fr";
    }

    public string T(string key, string language, params object[] args)
    {
        if (!Messages.TryGetValue(key, out var messageSet))
        {
            return key;
        }

        var template = language switch
        {
            "ar" => messageSet.Ar,
            "en" => messageSet.En,
            _ => messageSet.Fr
        };

        return args.Length == 0
            ? template
            : string.Format(CultureInfo.InvariantCulture, template, args);
    }

    private static string NormalizeLanguage(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = value.Trim().ToLowerInvariant();
        if (normalized.StartsWith("fr", StringComparison.Ordinal))
        {
            return "fr";
        }

        if (normalized.StartsWith("ar", StringComparison.Ordinal))
        {
            return "ar";
        }

        if (normalized.StartsWith("en", StringComparison.Ordinal))
        {
            return "en";
        }

        return normalized;
    }
}
