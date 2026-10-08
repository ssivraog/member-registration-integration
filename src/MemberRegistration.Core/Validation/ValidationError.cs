namespace MemberRegistration.Core.Validation;

/// <summary>One problem with a registration. Messages never echo the submitted value (it is PII).</summary>
public sealed record ValidationError(string Field, string Code, string Message);
