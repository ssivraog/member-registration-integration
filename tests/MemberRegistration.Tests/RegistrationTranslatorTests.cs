using System.Text.Json.Nodes;
using MemberRegistration.Core;
using MemberRegistration.Core.Contracts;
using MemberRegistration.Core.Validation;
using static MemberRegistration.Tests.TestSupport;

namespace MemberRegistration.Tests;

public class RegistrationTranslatorTests
{
    private static string TranslateToJson(string json)
    {
        var result = Translator().Translate(json);
        var success = Assert.IsType<TranslationResult.Success>(result);
        return LegacyJson.Serialize(success.Request);
    }

    private static TranslationResult.Failure TranslateToFailure(string? json) =>
        Assert.IsType<TranslationResult.Failure>(Translator().Translate(json));

    // ----- Valid registration -------------------------------------------------------------------

    [Fact]
    public void Sample_registration_produces_the_legacy_contract_exactly()
    {
        // String equality also proves property order and nesting, not just the values.
        Assert.Equal(Minified(ExpectedLegacyPayload), TranslateToJson(SampleRegistration));
    }

    [Fact]
    public void Source_is_always_the_fixed_literal_even_if_the_input_sends_one()
    {
        var json = Registration(r => r["source"] = "WEBSITE");

        var member = JsonNode.Parse(TranslateToJson(json))!["member"]!;

        Assert.Equal("MILKYWAY", member["source"]!.GetValue<string>());
    }

    [Fact]
    public void Values_are_trimmed()
    {
        var json = Registration(r =>
        {
            r["firstName"] = "  Alex ";
            r["lastName"] = "\tNguyen\n";
            r["email"] = " alex.nguyen@example.com ";
        });

        Assert.Equal(Minified(ExpectedLegacyPayload), TranslateToJson(json));
    }

    [Fact]
    public void Non_ascii_names_and_plus_addressing_are_written_unescaped()
    {
        var json = Registration(r =>
        {
            r["firstName"] = "Zoë";
            r["lastName"] = "O'Brien-Nguyễn";
            r["email"] = "zoe+test@example.com.au";
        });

        var output = TranslateToJson(json);

        Assert.Contains("\"given_name\":\"Zoë\"", output);
        Assert.Contains("\"family_name\":\"O'Brien-Nguyễn\"", output);
        Assert.Contains("\"email\":\"zoe+test@example.com.au\"", output);
    }

    // ----- Unexpected extra fields --------------------------------------------------------------

    [Fact]
    public void Unexpected_extra_fields_are_ignored()
    {
        var json = Registration(r =>
        {
            r["middleName"] = "James";
            r["marketingOptIn"] = true;
            r["address"] = new JsonObject { ["postcode"] = "3000", ["lines"] = new JsonArray("1 Main St") };
            r["tags"] = new JsonArray(1, 2, 3);
            r["notes"] = null;
        });

        Assert.Equal(Minified(ExpectedLegacyPayload), TranslateToJson(json));
    }

    [Fact]
    public void RegisteredAt_is_not_required()
    {
        var json = Registration(r => r.Remove("registeredAt"));

        Assert.Equal(Minified(ExpectedLegacyPayload), TranslateToJson(json));
    }

    [Fact]
    public void Field_names_are_matched_exactly_so_a_differently_cased_name_is_an_extra_field()
    {
        var json = Registration(r =>
        {
            r.Remove("firstName");
            r["FirstName"] = "Alex";
        });

        var error = Assert.Single(TranslateToFailure(json).Errors);
        Assert.Equal(new ValidationError(FieldNames.FirstName, ErrorCodes.Required, "firstName is required."), error);
    }

    // ----- Required fields ----------------------------------------------------------------------

    public static TheoryData<string, string> MissingOrEmpty()
    {
        var data = new TheoryData<string, string>();
        string[] fields =
        [
            FieldNames.FirstName, FieldNames.LastName, FieldNames.DateOfBirth, FieldNames.Email,
            FieldNames.MembershipType,
        ];
        foreach (var field in fields)
        {
            foreach (var variant in new[] { "missing", "null", "empty", "whitespace" })
            {
                data.Add(field, variant);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(MissingOrEmpty))]
    public void Missing_or_empty_required_field_is_a_required_error(string field, string variant)
    {
        var json = Registration(r =>
        {
            switch (variant)
            {
                case "missing": r.Remove(field); break;
                case "null": r[field] = null; break;
                case "empty": r[field] = ""; break;
                case "whitespace": r[field] = "   "; break;
            }
        });

        var failure = TranslateToFailure(json);

        Assert.Equal(FailureKind.Invalid, failure.Kind);
        var error = Assert.Single(failure.Errors);
        Assert.Equal(field, error.Field);
        Assert.Equal(ErrorCodes.Required, error.Code);
        Assert.Equal($"{field} is required.", error.Message);
    }

    [Fact]
    public void Empty_object_reports_every_required_field_in_order()
    {
        var failure = TranslateToFailure("{}");

        Assert.Equal(
            [FieldNames.FirstName, FieldNames.LastName, FieldNames.DateOfBirth, FieldNames.Email, FieldNames.MembershipType],
            failure.Errors.Select(e => e.Field));
        Assert.All(failure.Errors, e => Assert.Equal(ErrorCodes.Required, e.Code));
    }

    // ----- Wrong JSON types ---------------------------------------------------------------------

    public static TheoryData<string, string> WrongTypes() => new()
    {
        { FieldNames.FirstName, "123" },
        { FieldNames.LastName, "true" },
        { FieldNames.DateOfBirth, "19900412" },
        { FieldNames.Email, "{}" },
        { FieldNames.MembershipType, "[\"Single\"]" },
    };

    [Theory]
    [MemberData(nameof(WrongTypes))]
    public void Non_string_value_is_an_invalid_type_error_not_an_exception(string field, string jsonValue)
    {
        var json = Registration(r => r[field] = JsonNode.Parse(jsonValue));

        var error = Assert.Single(TranslateToFailure(json).Errors);

        Assert.Equal(new ValidationError(field, ErrorCodes.InvalidType, $"{field} must be a string."), error);
    }

    // ----- Email --------------------------------------------------------------------------------

    [Theory]
    [InlineData("alex")]
    [InlineData("alex@")]
    [InlineData("@example.com")]
    [InlineData("alex@example")]
    [InlineData("alex@@example.com")]
    [InlineData("al ex@example.com")]
    [InlineData("alex@example..com")]
    [InlineData("alex@.example.com")]
    [InlineData("alex@example.com.")]
    public void Implausible_email_is_an_invalid_email_error(string email)
    {
        var json = Registration(r => r["email"] = email);

        var error = Assert.Single(TranslateToFailure(json).Errors);

        Assert.Equal(FieldNames.Email, error.Field);
        Assert.Equal(ErrorCodes.InvalidEmail, error.Code);
    }

    // ----- Membership type ----------------------------------------------------------------------

    [Theory]
    [InlineData("Gold")]
    [InlineData("Singles")]
    [InlineData("S")]
    [InlineData("Single Parent")]
    public void Unknown_membership_type_is_an_invalid_membership_type_error(string membershipType)
    {
        var json = Registration(r => r["membershipType"] = membershipType);

        var error = Assert.Single(TranslateToFailure(json).Errors);

        Assert.Equal(new ValidationError(FieldNames.MembershipType, ErrorCodes.InvalidMembershipType,
            "membershipType must be one of: Single, Couple, Family."), error);
    }

    [Theory]
    [InlineData("Single", "S")]
    [InlineData("Couple", "C")]
    [InlineData("Family", "F")]
    [InlineData("family", "F")]
    [InlineData(" COUPLE ", "C")]
    public void Membership_type_maps_to_plan_code_in_the_payload(string membershipType, string planCode)
    {
        var json = Registration(r => r["membershipType"] = membershipType);

        var member = JsonNode.Parse(TranslateToJson(json))!["member"]!;

        Assert.Equal(planCode, member["plan_code"]!.GetValue<string>());
    }

    // ----- Date of birth through the whole mapping ----------------------------------------------

    [Theory]
    [InlineData("1990-04-12", "12/04/1990")]
    [InlineData("2001-01-05", "05/01/2001")]
    [InlineData("2000-02-29", "29/02/2000")]
    [InlineData("1990-04-12T00:00:00Z", "12/04/1990")]
    [InlineData("1990-04-12T23:30:00-10:00", "12/04/1990")]
    public void Date_of_birth_is_written_as_dd_MM_yyyy(string dateOfBirth, string expectedDob)
    {
        var json = Registration(r => r["dateOfBirth"] = dateOfBirth);

        var member = JsonNode.Parse(TranslateToJson(json))!["member"]!;

        Assert.Equal(expectedDob, member["dob"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("12/04/1990")]
    [InlineData("04/12/1990")]
    [InlineData("12-04-1990")]
    public void Date_of_birth_in_a_different_format_is_an_invalid_date_format_error(string dateOfBirth)
    {
        var json = Registration(r => r["dateOfBirth"] = dateOfBirth);

        var error = Assert.Single(TranslateToFailure(json).Errors);

        Assert.Equal(new ValidationError(FieldNames.DateOfBirth, ErrorCodes.InvalidDateFormat,
            "dateOfBirth must be in yyyy-MM-dd format."), error);
    }

    // ----- Multiple errors and body shape -------------------------------------------------------

    [Fact]
    public void Every_error_is_reported_together()
    {
        var json = Registration(r =>
        {
            r.Remove("lastName");
            r["dateOfBirth"] = "12/04/1990";
            r["email"] = "not-an-email";
            r["membershipType"] = "Gold";
        });

        var failure = TranslateToFailure(json);

        Assert.Equal(
            [
                (FieldNames.LastName, ErrorCodes.Required),
                (FieldNames.DateOfBirth, ErrorCodes.InvalidDateFormat),
                (FieldNames.Email, ErrorCodes.InvalidEmail),
                (FieldNames.MembershipType, ErrorCodes.InvalidMembershipType),
            ],
            failure.Errors.Select(e => (e.Field, e.Code)));
    }

    [Fact]
    public void Error_messages_never_echo_the_submitted_values()
    {
        var json = Registration(r =>
        {
            r["email"] = "secret-email-value";
            r["dateOfBirth"] = "secret-dob-value";
            r["membershipType"] = "secret-type-value";
        });

        var failure = TranslateToFailure(json);

        Assert.All(failure.Errors, e => Assert.DoesNotContain("secret", e.Message));
    }

    [Theory]
    [InlineData(null, ErrorCodes.BodyEmpty)]
    [InlineData("", ErrorCodes.BodyEmpty)]
    [InlineData("   ", ErrorCodes.BodyEmpty)]
    [InlineData("{", ErrorCodes.BodyMalformed)]
    [InlineData("{\"firstName\": \"Alex\",}", ErrorCodes.BodyMalformed)]
    [InlineData("{\"email\": \"a@b.com\", \"email\": \"c@d.com\"}", ErrorCodes.BodyMalformed)]
    [InlineData("[]", ErrorCodes.BodyNotObject)]
    [InlineData("\"text\"", ErrorCodes.BodyNotObject)]
    [InlineData("42", ErrorCodes.BodyNotObject)]
    [InlineData("null", ErrorCodes.BodyNotObject)]
    public void Unreadable_body_is_a_malformed_body_failure(string? body, string expectedCode)
    {
        var failure = TranslateToFailure(body);

        Assert.Equal(FailureKind.MalformedBody, failure.Kind);
        var error = Assert.Single(failure.Errors);
        Assert.Equal(FieldNames.Body, error.Field);
        Assert.Equal(expectedCode, error.Code);
    }
}
