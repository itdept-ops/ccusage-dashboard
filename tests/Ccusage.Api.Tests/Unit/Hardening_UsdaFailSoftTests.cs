using System.Net;
using Ccusage.Api.Services;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Ccusage.Api.Tests.Unit;

/// <summary>
/// Hardening: USDA FoodData Central must fail SOFT like the other food providers. On a transient
/// non-success status (500/429/etc.) <see cref="UsdaFoodService"/> must NOT throw — search returns an
/// empty list (so the endpoint falls through to the FatSecret fallback) and details returns null (so the
/// endpoint returns 404), never surfacing a 500. The previous behaviour called
/// <c>EnsureSuccessStatusCode()</c>, which threw and bubbled up as a 500.
/// </summary>
public class Hardening_UsdaFailSoftTests
{
    /// <summary>Returns the supplied status with a tiny JSON body for every request (no key inspection).</summary>
    private sealed class StatusHandler(HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent("{\"error\":\"upstream\"}"),
            });
    }

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private static UsdaFoodService ServiceReturning(HttpStatusCode status) =>
        new(
            new StubFactory(new StatusHandler(status)),
            Options.Create(new UsdaOptions { ApiKey = "test-key" }),
            new MemoryCache(new MemoryCacheOptions()),
            NullLogger<UsdaFoodService>.Instance);

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task SearchAsync_returns_empty_on_a_transient_usda_error_so_the_endpoint_falls_back(
        HttpStatusCode status)
    {
        var usda = ServiceReturning(status);

        var act = async () => await usda.SearchAsync("chicken", barcode: null);

        var result = (await act.Should().NotThrowAsync()).Subject;
        result.Should().BeEmpty();
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.BadGateway)]
    public async Task GetDetailsAsync_returns_null_on_a_transient_usda_error_so_the_endpoint_404s_not_500(
        HttpStatusCode status)
    {
        var usda = ServiceReturning(status);

        var act = async () => await usda.GetDetailsAsync(123456);

        var result = (await act.Should().NotThrowAsync()).Subject;
        result.Should().BeNull();
    }
}
