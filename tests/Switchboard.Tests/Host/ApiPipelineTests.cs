using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Switchboard.Host.Web;
using Switchboard.Tests.Support;

namespace Switchboard.Tests.Host;

/// <summary>
/// What every API route shares: JSON is required on a state-changing method before any endpoint runs (a cross-site form cannot send it without a
/// CORS preflight, which the add-on never answers), one reader and one limit mechanism for every body, one error document, and the same ingress and
/// CSP rules on all of it.
/// </summary>
public sealed class ApiPipelineTests
{
    private const string Supervisor = "172.30.32.2";

    private static readonly string[] Writes =
    [
        "/api/control", "/api/control/confirm", "/api/guard/kill-switch", "/api/devices/heater/override", "/api/goals", "/api/ev/mode",
        "/api/ev/charge-now", "/api/setup/validate", "/api/setup/save", "/api/setup/self-test", "/api/setup/active-test",
    ];

    public static TheoryData<string, string> NonJsonWrites()
    {
        var data = new TheoryData<string, string>();
        foreach (var path in Writes)
        {
            data.Add(path, "text/plain");
            data.Add(path, "application/x-www-form-urlencoded");
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(NonJsonWrites))]
    public async Task A_post_without_a_json_content_type_is_refused_with_400_before_the_endpoint_runs(string path, string contentType)
    {
        // Why: the one rule that keeps another page from changing anything here; it must hold for every state-changing route, with a coded refusal the page can read.
        using var root = new TempWebRoot();
        using var factory = new SwitchboardHostFactory(root.Path);
        _ = factory.Server;

        var refused = await Send(factory, "POST", path, contentType, "{}");

        Assert.Equal(StatusCodes.Status400BadRequest, refused.Response.StatusCode);
        Assert.StartsWith("application/problem+json", refused.Response.ContentType, StringComparison.Ordinal);
        var body = Json(refused);
        Assert.Equal("bad_request", body.GetProperty("code").GetString());
        Assert.Equal(400, body.GetProperty("status").GetInt32());
    }

    [Fact]
    public async Task A_delete_needs_no_content_type_and_an_unknown_post_stays_a_404()
    {
        // Why: a DELETE has no body to be JSON, and the group's rule must not turn the unknown-route 404 into a 400.
        using var root = new TempWebRoot();
        using var factory = new SwitchboardHostFactory(root.Path);
        _ = factory.Server;

        var deleted = await Send(factory, "DELETE", "/api/ev/charge-now");
        var unknown = await Send(factory, "POST", "/api/nothing_here", "text/plain", "x");

        Assert.Equal(StatusCodes.Status404NotFound, deleted.Response.StatusCode);
        Assert.Equal(StatusCodes.Status404NotFound, unknown.Response.StatusCode);
    }

    [Fact]
    public async Task A_peer_outside_ingress_is_refused_on_a_write_before_the_json_rule_and_the_csp_header_is_there()
    {
        // Why: the ingress policy outranks the route group, and the CSP header is on every answer, refusals included.
        using var root = new TempWebRoot();
        using var factory = new SwitchboardHostFactory(root.Path);
        _ = factory.Server;

        var refused = await Send(factory, "POST", "/api/control", "text/plain", "x", from: "10.0.0.5");
        var answered = await Send(factory, "POST", "/api/control", "text/plain", "x");

        Assert.Equal(StatusCodes.Status403Forbidden, refused.Response.StatusCode);
        Assert.Equal(StatusCodes.Status400BadRequest, answered.Response.StatusCode);
        Assert.Contains("frame-ancestors 'self'", refused.Response.Headers.ContentSecurityPolicy.ToString(), StringComparison.Ordinal);
        Assert.Contains("frame-ancestors 'self'", answered.Response.Headers.ContentSecurityPolicy.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/api/control", 1)]
    [InlineData("/api/setup/validate", 1)]
    [InlineData("/api/goals", 1)]
    [InlineData("/api/ev/mode", 1)]
    public async Task A_body_past_its_limit_is_refused_413_when_chunked_too_and_the_server_limit_is_the_global_one(string path, int pastLimit)
    {
        // Why: one reader decides for every endpoint. Without a declared length only the read sees the body pass the limit; it is a refused body, not an exception.
        using var root = new TempWebRoot();
        using var factory = new SwitchboardHostFactory(root.Path);
        _ = factory.Server;
        var global = factory.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<Switchboard.Application.Options.IngressOptions>>().Value.MaxRequestBodyBytes;
        var limit = path is "/api/goals" ? 1024 : path is "/api/ev/mode" ? 512 : global;
        var body = Encoding.UTF8.GetBytes("{}" + new string(' ', checked((int)limit - 2 + pastLimit)));
        var size = new BodySizeFeature();

        var context = await factory.Server.SendAsync(request =>
        {
            request.Request.Method = "POST";
            request.Request.Path = path;
            request.Connection.RemoteIpAddress = IPAddress.Parse(Supervisor);
            request.Request.ContentType = "application/json";
            request.Features.Set<IHttpMaxRequestBodySizeFeature>(size);
            request.Request.Body = new ChunkedBody(body, size);
        }, TestContext.Current.CancellationToken);

        Assert.Equal(global, size.MaxRequestBodySize);

        // EV answers a body past its bound as a malformed one, as it always did; the others as 413.
        Assert.Equal(path == "/api/ev/mode" ? StatusCodes.Status400BadRequest : StatusCodes.Status413PayloadTooLarge, context.Response.StatusCode);
    }

    [Fact]
    public async Task A_body_exactly_at_the_endpoint_limit_is_read()
    {
        // Why: the limit is inclusive of its last byte; an off-by-one here would refuse a legitimate full-size request.
        using var root = new TempWebRoot();
        using var factory = new SwitchboardHostFactory(root.Path);
        _ = factory.Server;
        var json = "{\"mode\":\"auto\"}";
        var body = json + new string(' ', 512 - json.Length);

        var atLimit = await Send(factory, "POST", "/api/ev/mode", "application/json", body);
        var over = await Send(factory, "POST", "/api/ev/mode", "application/json", body + " ");

        // Not configured in this host: the body was understood (404 from the module), where an oversized one is a 400.
        Assert.Equal(StatusCodes.Status404NotFound, atLimit.Response.StatusCode);
        Assert.Equal(StatusCodes.Status400BadRequest, over.Response.StatusCode);
    }

    [Fact]
    public async Task A_status_without_a_body_is_answered_with_a_problem_document()
    {
        // Why: one error format. The unknown API route and the refused peer had an empty body; both read as the same document now.
        using var root = new TempWebRoot();
        using var factory = new SwitchboardHostFactory(root.Path);
        _ = factory.Server;

        var unknown = await Send(factory, "GET", "/api/nothing_here");

        Assert.Equal(StatusCodes.Status404NotFound, unknown.Response.StatusCode);
        Assert.Equal(404, Json(unknown).GetProperty("status").GetInt32());
    }

    [Fact]
    public async Task The_change_helper_answers_with_the_status_that_showed_the_change_and_lets_go_of_the_feed()
    {
        // Why: the API answers with the state the guard evaluated, so a change must wait for the status that shows it, whether that status came before or after.
        var feed = new Feed(1);
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();

        var before = await StatusChange.ApplyAsync(feed.Of(), s => s == 5, () => { feed.Value = 5; return "set"; }, _ => true, TimeSpan.FromSeconds(1), time, TestContext.Current.CancellationToken);
        var waiting = StatusChange.ApplyAsync(feed.Of(), s => s == 7, () => "set", _ => true, TimeSpan.FromSeconds(1), time, TestContext.Current.CancellationToken);
        Assert.False(waiting.IsCompleted);
        feed.Publish(7);
        var after = await waiting;

        Assert.Equal(("set", 5, false), (before.Result, before.Status, before.Late));
        Assert.Equal(("set", 7, false), (after.Result, after.Status, after.Late));
        Assert.Equal(0, feed.Listeners);
    }

    [Fact]
    public async Task The_change_helper_reports_a_late_evaluation_and_does_not_wait_for_a_change_that_changed_nothing()
    {
        // Why: a refused change is answered at once (nothing to wait for); a change nobody evaluated in time is in force anyway, and is reported as late, never as failed.
        var feed = new Feed(1);
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();

        var refused = await StatusChange.ApplyAsync(feed.Of(), s => s == 9, () => "refused", result => result != "refused", TimeSpan.FromSeconds(1), time, TestContext.Current.CancellationToken);
        var late = StatusChange.ApplyAsync(feed.Of(), s => s == 9, () => "set", _ => true, TimeSpan.FromSeconds(1), time, TestContext.Current.CancellationToken);
        time.Advance(TimeSpan.FromSeconds(2));
        var lateResult = await late;

        Assert.Equal(("refused", false), (refused.Result, refused.Late));
        Assert.Equal(("set", true), (lateResult.Result, lateResult.Late));
        Assert.Equal(0, feed.Listeners);
    }

    private sealed class Feed(int initial)
    {
        private EventHandler<int>? _published;

        public int Value { get; set; } = initial;

        public int Listeners => _published?.GetInvocationList().Length ?? 0;

        public StatusFeed<int> Of() => new(h => _published += h, h => _published -= h, () => Value);

        public void Publish(int value)
        {
            Value = value;
            _published?.Invoke(this, value);
        }
    }

    private static JsonElement Json(HttpContext context) => SetupEndpointTests.Json(context);

    private static Task<HttpContext> Send(SwitchboardHostFactory factory, string method, string path, string? contentType = null, string? body = null, string from = Supervisor) =>
        factory.Server.SendAsync(context =>
        {
            context.Request.Method = method;
            context.Request.Path = path;
            context.Connection.RemoteIpAddress = IPAddress.Parse(from);
            if (contentType is not null)
            {
                context.Request.ContentType = contentType;
                context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body ?? string.Empty));
            }
        });

    private sealed class BodySizeFeature : IHttpMaxRequestBodySizeFeature
    {
        public bool IsReadOnly => false;

        public long? MaxRequestBodySize { get; set; } = 30_000_000;
    }

    /// <summary>A body without a declared length, read as Kestrel reads one: a read that takes it past the request's limit throws 413.</summary>
    private sealed class ChunkedBody(byte[] content, IHttpMaxRequestBodySizeFeature size) : MemoryStream(content)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Position + Math.Min(buffer.Length, Length - Position) > size.MaxRequestBodySize)
            {
                throw new BadHttpRequestException("Request body too large.", StatusCodes.Status413PayloadTooLarge);
            }

            return base.ReadAsync(buffer, cancellationToken);
        }
    }
}
