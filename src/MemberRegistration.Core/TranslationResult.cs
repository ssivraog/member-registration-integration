using MemberRegistration.Core.Contracts;
using MemberRegistration.Core.Validation;

namespace MemberRegistration.Core;

public enum FailureKind
{
    /// <summary>The body could not be read as a JSON object at all.</summary>
    MalformedBody,

    /// <summary>The body was read, but one or more fields failed validation.</summary>
    Invalid,
}

public abstract record TranslationResult
{
    private TranslationResult() { }

    public sealed record Success(LegacyMemberRequest Request) : TranslationResult;

    public sealed record Failure(FailureKind Kind, IReadOnlyList<ValidationError> Errors) : TranslationResult;
}
