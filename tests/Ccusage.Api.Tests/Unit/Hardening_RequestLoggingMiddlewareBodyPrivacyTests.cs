using System.Text;
using Ccusage.Api.Data.Entities;
using Ccusage.Api.Infrastructure;
using FluentAssertions;
using Microsoft.AspNetCore.Http;

namespace Ccusage.Api.Tests.Unit;

/// <summary>
/// Privacy hardening: the activity log must NOT persist request/response bodies for the email-bearing
/// admin paths (/api/users, /api/audit, /api/access-policy, /api/chat/contacts, /api/chat/directory),
/// because those bodies carry OTHER users' emails and the Activity page replays stored bodies to any
/// holder of activity.view. The request LINE (method/path/status/timing/bytes) must still be logged so
/// the log keeps its diagnostic value. Drives RequestLoggingMiddleware directly for determinism (the
/// real persistence path runs on a background writer).
/// </summary>
public class Hardening_RequestLoggingMiddlewareBodyPrivacyTests
{
    private const string OtherUserEmail = "victim.other-user@example.com";

    // The downstream app echoes an admin-style JSON body containing another user's email.
    private static async Task<RequestLog?> RunThrough(string path, string method = "GET")
    {
        var queue = new RequestLogQueue();
        var responsePayload = $"[{{\"email\":\"{OtherUserEmail}\",\"name\":\"Victim\"}}]";

        var middleware = new RequestLoggingMiddleware(async ctx =>
        {
            ctx.Response.StatusCode = StatusCodes.Status200OK;
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync(responsePayload);
        }, queue);

        var ctx = new DefaultHttpContext();
        ctx.Request.Method = method;
        ctx.Request.Path = path;
        // A request body that also carries another user's email (e.g. POST /api/users).
        var reqBytes = Encoding.UTF8.GetBytes($"{{\"email\":\"{OtherUserEmail}\"}}");
        ctx.Request.Body = new MemoryStream(reqBytes);
        ctx.Request.ContentLength = reqBytes.Length;
        ctx.Request.ContentType = "application/json";
        ctx.Response.Body = new MemoryStream();

        await middleware.Invoke(ctx);

        return queue.Reader.TryRead(out var entry) ? entry : null;
    }

    [Theory]
    [InlineData("/api/users")]
    [InlineData("/api/users/42")]
    [InlineData("/api/audit")]
    [InlineData("/api/access-policy")]
    [InlineData("/api/chat/contacts")]
    [InlineData("/api/chat/contacts/someone@x.com")]
    [InlineData("/api/chat/directory")]
    public async Task Email_bearing_admin_paths_log_the_request_line_but_never_the_bodies(string path)
    {
        var entry = await RunThrough(path, method: "POST");

        entry.Should().NotBeNull("the request line must still be logged for diagnostics");
        entry!.Path.Should().Be(path);
        entry.Method.Should().Be("POST");
        entry.StatusCode.Should().Be(200);

        // The crux: no other-user email may reach a stored body.
        entry.RequestBody.Should().BeNull();
        entry.ResponseBody.Should().BeNull();
        entry.RequestBody.Should().NotContain(OtherUserEmail);
        entry.ResponseBody.Should().NotContain(OtherUserEmail);
    }

    [Fact]
    public async Task Non_admin_paths_still_capture_bodies()
    {
        // A normal route is unaffected: bodies are still captured (preserving the log's value).
        var entry = await RunThrough("/api/usage", method: "GET");

        entry.Should().NotBeNull();
        entry!.ResponseBody.Should().Contain(OtherUserEmail);
    }
}
