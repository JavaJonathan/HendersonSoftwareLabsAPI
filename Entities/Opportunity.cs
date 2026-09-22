namespace HendersonSoftwareLabsAPI.Entities;

public enum OpportunityEntityType { ActiveProject, BusinessProspect }
public enum ActiveProjectSourceType { ExplicitDemand, OperationalSignal }
public enum ActiveProjectKind { ExplicitDemand, OperationalSignal, FullTimeRole, Other }
public enum ActiveProjectDecision { Pursue, Investigate, Pass }
public enum BusinessProspectDecision { Prioritize, Watch, Skip }
public enum OpportunityRecommendation { Pursue, Investigate, Pass, Prioritize, Watch, Skip }
public enum BudgetStatus { Unknown, Compatible, Incompatible }
public enum EvaluationStatus { Ready, Failed, Stale }
public enum EvaluationProvider { Simulated, Jev }
public enum PriorityBand { High, Medium, Low }
public enum IncompleteInformationTolerance { Low, Medium, High }

public class Opportunity
{
    public int Id { get; set; }
    public OpportunityEntityType EntityType { get; set; }
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string? SourceName { get; set; }
    public string? SourceUrl { get; set; }
    public DateTime? SourceDate { get; set; }
    public string? ExternalId { get; set; }
    public string SourcePassagesJson { get; set; } = "[]";
    public string Fingerprint { get; set; } = "";
    public int? DuplicateOfId { get; set; }
    public Opportunity? DuplicateOf { get; set; }
    public bool IsSynthetic { get; set; }
    public string? SyntheticKey { get; set; }
    public string Notes { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public List<OpportunityEvaluation> Evaluations { get; set; } = [];
    public ActiveProjectDetail? ActiveProjectDetail { get; set; }
    public BusinessProspectDetail? BusinessProspectDetail { get; set; }
}

public class ActiveProjectDetail
{
    public int OpportunityId { get; set; }
    public Opportunity Opportunity { get; set; } = null!;
    public ActiveProjectSourceType DeclaredSourceType { get; set; } = ActiveProjectSourceType.ExplicitDemand;
    public ActiveProjectDecision? UserDecision { get; set; }
}

public class BusinessProspectDetail
{
    public int OpportunityId { get; set; }
    public Opportunity Opportunity { get; set; } = null!;
    public string NormalizedBusinessName { get; set; } = "";
    public string? WebsiteUrl { get; set; }
    public string? NormalizedWebsiteDomain { get; set; }
    public string? Geography { get; set; }
    public string? Industry { get; set; }
    public BusinessProspectDecision? UserDecision { get; set; }
}

public class OpportunityEvaluation
{
    public int Id { get; set; }
    public int OpportunityId { get; set; }
    public Opportunity Opportunity { get; set; } = null!;
    public EvaluationProvider Provider { get; set; }
    public EvaluationStatus Status { get; set; }
    public string Model { get; set; } = "simulation-v1";
    public string QuestionSetVersion { get; set; } = "radar-v1";
    public OpportunityRecommendation? Recommendation { get; set; }
    public PriorityBand? PriorityBand { get; set; }
    public BudgetStatus BudgetStatus { get; set; }
    public string AssessmentJson { get; set; } = "{}";
    public string ResultJson { get; set; } = "{}";
    public string ProviderResponseJson { get; set; } = "{}";
    public string Summary { get; set; } = "";
    public string NextStep { get; set; } = "";
    public int? InputTokens { get; set; }
    public int? OutputTokens { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class RadarPreferences
{
    public int Id { get; set; }
    public string OwnerUserId { get; set; } = "";
    public ApplicationUser OwnerUser { get; set; } = null!;
    public string ActiveProjectPreferencesJson { get; set; } = "{}";
    public string BusinessProspectPreferencesJson { get; set; } = "{}";
    public int DigestActiveProjectCount { get; set; } = 3;
    public int DigestBusinessProspectCount { get; set; } = 2;
    public DateTime UpdatedAt { get; set; }
}
