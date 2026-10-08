using MemberRegistration.Core.Validation;

namespace MemberRegistration.Core.Parsing;

public abstract record ReadResult
{
    private ReadResult() { }

    public sealed record Read(RawRegistration Registration) : ReadResult;

    public sealed record Unreadable(ValidationError Error) : ReadResult;
}
