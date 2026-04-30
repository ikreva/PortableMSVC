namespace PortableMSVC;

public sealed record PlanIssue(string Severity, string Code, string Message, string? Suggestion = null);
