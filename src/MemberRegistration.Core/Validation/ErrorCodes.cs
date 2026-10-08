namespace MemberRegistration.Core.Validation;

public static class ErrorCodes
{
    public const string BodyEmpty = "body_empty";
    public const string BodyMalformed = "body_malformed";
    public const string BodyNotObject = "body_not_object";
    public const string InvalidType = "invalid_type";
    public const string Required = "required";
    public const string InvalidEmail = "invalid_email";
    public const string InvalidDateFormat = "invalid_date_format";
    public const string InvalidDate = "invalid_date";
    public const string InvalidMembershipType = "invalid_membership_type";
}
