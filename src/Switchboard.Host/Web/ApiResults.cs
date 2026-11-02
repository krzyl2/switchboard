using Microsoft.AspNetCore.Http.HttpResults;

namespace Switchboard.Host.Web;

/// <summary>
/// The one error answer of the API: a problem document (RFC 9457) that names what went wrong in
/// <c>detail</c>. A refusal the page branches on also carries <c>code</c> (the machine-readable
/// reason, the key the SPA reads) and <c>message</c> (the same sentence, as the page and the tests
/// have always read it).
/// </summary>
internal static class ApiResults
{
    public const string BadRequestCode = "bad_request";

    /// <summary>An error with no code: the client acts on the status alone.</summary>
    public static ProblemHttpResult Problem(int status, string detail) => TypedResults.Problem(detail, statusCode: status);

    /// <summary>An error the page words by its <paramref name="code"/>.</summary>
    public static ProblemHttpResult Refusal(int status, string code, string message) => TypedResults.Problem(
        message,
        statusCode: status,
        extensions: new Dictionary<string, object?>(StringComparer.Ordinal) { ["code"] = code, ["message"] = message });

    public static ProblemHttpResult TooLarge(long limit) =>
        Refusal(StatusCodes.Status413PayloadTooLarge, BadRequestCode, $"The body is larger than {limit} bytes.");
}
