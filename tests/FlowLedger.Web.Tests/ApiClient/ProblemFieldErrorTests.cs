using FlowLedger.Web.ApiClient;
using FluentAssertions;

namespace FlowLedger.Web.Tests.ApiClient;

public sealed class ProblemFieldErrorTests
{
    [Fact]
    public void Validation_errors_map_to_the_first_message_per_field_case_insensitively()
    {
        var doc = ProblemDocument.Parse("""{"title":"Bad","errors":{"Name":["Name is required.","too short"],"currency":["Use a 3-letter code."]}}""");

        doc.FieldErrors.Should().NotBeNull();
        var ex = new ApiClientException("x", fieldErrors: doc.FieldErrors);
        ex.FieldError("name").Should().Be("Name is required.");
        ex.FieldError("Currency").Should().Be("Use a 3-letter code.");
        ex.FieldError("missing").Should().BeNull();
    }

    [Fact]
    public void Problems_without_an_errors_map_have_no_field_errors()
    {
        ProblemDocument.Parse("""{"title":"Nope","detail":"d"}""").FieldErrors.Should().BeNull();
        new ApiClientException("x").FieldError("name").Should().BeNull();
    }
}
