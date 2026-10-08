using MemberRegistration.Core.Contracts;
using MemberRegistration.Core.Mapping;
using MemberRegistration.Core.Parsing;
using MemberRegistration.Core.Validation;

namespace MemberRegistration.Core;

/// <summary>CRM registration JSON in, legacy membership request out. Validates fully before mapping.</summary>
public sealed class RegistrationTranslator(TimeProvider clock)
{
    public const string Source = "MILKYWAY";

    private readonly DateOfBirthParser _dateOfBirthParser = new(clock);

    public TranslationResult Translate(string? json) => RegistrationReader.Read(json) switch
    {
        ReadResult.Read read => Validate(read.Registration),
        ReadResult.Unreadable unreadable => new TranslationResult.Failure(FailureKind.MalformedBody, [unreadable.Error]),
        _ => throw new InvalidOperationException("Unknown read result."),
    };

    private TranslationResult Validate(RawRegistration raw)
    {
        var errors = new List<ValidationError>();

        string? Required(string field, string? value)
        {
            if (raw.WrongTypeFields.Contains(field))
            {
                errors.Add(new ValidationError(field, ErrorCodes.InvalidType, $"{field} must be a string."));
                return null;
            }

            if (string.IsNullOrWhiteSpace(value))
            {
                errors.Add(new ValidationError(field, ErrorCodes.Required, $"{field} is required."));
                return null;
            }

            return value.Trim();
        }

        var firstName = Required(FieldNames.FirstName, raw.FirstName);
        var lastName = Required(FieldNames.LastName, raw.LastName);

        var dateOfBirth = default(DateOnly);
        if (Required(FieldNames.DateOfBirth, raw.DateOfBirth) is { } dobText
            && _dateOfBirthParser.TryParse(dobText, out dateOfBirth) is { } dobError)
        {
            errors.Add(dobError);
        }

        var email = Required(FieldNames.Email, raw.Email);
        if (email is not null && !EmailRule.IsPlausible(email))
        {
            errors.Add(new ValidationError(FieldNames.Email, ErrorCodes.InvalidEmail,
                "email is not a valid email address."));
        }

        var planCode = string.Empty;
        if (Required(FieldNames.MembershipType, raw.MembershipType) is { } membershipType
            && !PlanCodeMapper.TryMap(membershipType, out planCode))
        {
            errors.Add(new ValidationError(FieldNames.MembershipType, ErrorCodes.InvalidMembershipType,
                $"membershipType must be one of: {string.Join(", ", PlanCodeMapper.KnownTypes)}."));
        }

        if (errors.Count > 0)
        {
            return new TranslationResult.Failure(FailureKind.Invalid, errors);
        }

        // Every value below is set: a missing one would have added an error above.
        return new TranslationResult.Success(new LegacyMemberRequest(new LegacyMember(
            GivenName: firstName!,
            FamilyName: lastName!,
            Dob: DateOfBirthParser.ToLegacy(dateOfBirth),
            Contact: new LegacyContact(email!),
            PlanCode: planCode,
            Source: Source)));
    }
}
