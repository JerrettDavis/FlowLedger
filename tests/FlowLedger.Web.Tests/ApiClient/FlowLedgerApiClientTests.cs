using System.Net;
using System.Text;
using System.Text.Json;
using FlowLedger.Web.ApiClient;
using FluentAssertions;
using Microsoft.Extensions.Logging;

namespace FlowLedger.Web.Tests.ApiClient;

/// <summary>
/// Unit tests for <see cref="FlowLedgerApiClient"/> that verify:
/// 1. Malformed / non-JSON responses throw <see cref="ApiClientException"/> (not raw JsonException).
/// 2. Non-success HTTP status codes throw <see cref="ApiClientException"/>.
/// 3. <see cref="SyncResult"/> deserializes the API's actual property names correctly.
/// </summary>
public sealed class FlowLedgerApiClientTests
{
    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds a <see cref="FlowLedgerApiClient"/> whose HTTP transport is backed by a stub
    /// handler that always returns <paramref name="response"/>.
    /// </summary>
    private static FlowLedgerApiClient BuildClient(HttpResponseMessage response)
    {
        var handler = new StubHttpMessageHandler(response);
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost") };
        var logger = Microsoft.Extensions.Logging.Abstractions.NullLogger<FlowLedgerApiClient>.Instance;
        return new FlowLedgerApiClient(http, logger);
    }

    // ── Malformed JSON (HTML error page) tests ────────────────────────────────

    [Fact]
    public async Task GetAccountsAsync_WhenResponseIsHtml_ThrowsApiClientException()
    {
        var html = "<html><body>Something went wrong</body></html>";
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(html, Encoding.UTF8, "application/json")
        };

        var client = BuildClient(response);

        var act = () => client.GetAccountsAsync();

        await act.Should().ThrowAsync<ApiClientException>()
            .WithMessage("*unexpected response*");
    }

    [Fact]
    public async Task GetAccountsAsync_WhenResponseIsHtml_DoesNotThrowRawJsonException()
    {
        var html = "<html><body>Error</body></html>";
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(html, Encoding.UTF8, "application/json")
        };

        var client = BuildClient(response);

        // Must NOT bubble a raw JsonException — only ApiClientException is allowed.
        var act = () => client.GetAccountsAsync();

        await act.Should().ThrowAsync<ApiClientException>();
        await act.Should().NotThrowAsync<JsonException>();
    }

    [Fact]
    public async Task GetCategoriesAsync_WhenResponseIsHtml_ThrowsApiClientException()
    {
        var html = "<html><body>502 Bad Gateway</body></html>";
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(html, Encoding.UTF8, "application/json")
        };

        var client = BuildClient(response);

        var act = () => client.GetCategoriesAsync();

        await act.Should().ThrowAsync<ApiClientException>()
            .WithMessage("*unexpected response*");
    }

    [Fact]
    public async Task GetForecastAsync_WhenResponseIsHtml_ThrowsApiClientException()
    {
        var html = "<html><body>Internal Server Error</body></html>";
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(html, Encoding.UTF8, "application/json")
        };

        var client = BuildClient(response);

        var act = () => client.GetForecastAsync(months: 3);

        await act.Should().ThrowAsync<ApiClientException>()
            .WithMessage("*unexpected response*");
    }

    // ── Non-success status code tests ─────────────────────────────────────────

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.Unauthorized)]
    public async Task GetAccountsAsync_WhenNonSuccessStatus_ThrowsApiClientException(HttpStatusCode statusCode)
    {
        var response = new HttpResponseMessage(statusCode)
        {
            Content = new StringContent("error", Encoding.UTF8, "text/plain")
        };

        var client = BuildClient(response);

        var act = () => client.GetAccountsAsync();

        var exception = await act.Should().ThrowAsync<ApiClientException>();
        exception.Which.StatusCode.Should().Be(statusCode);
    }

    [Fact]
    public async Task GetTransactionsAsync_WhenServerReturns503_ThrowsApiClientExceptionWithStatusCode()
    {
        var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        {
            Content = new StringContent("Service unavailable", Encoding.UTF8, "text/plain")
        };

        var client = BuildClient(response);

        var act = () => client.GetTransactionsAsync();

        var exception = await act.Should().ThrowAsync<ApiClientException>();
        exception.Which.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
    }

    [Fact]
    public async Task GetRecurringFlowsAsync_WhenServerReturns500_ThrowsApiClientException()
    {
        var response = new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("{\"error\":\"Internal Server Error\"}", Encoding.UTF8, "application/json")
        };

        var client = BuildClient(response);

        var act = () => client.GetRecurringFlowsAsync();

        await act.Should().ThrowAsync<ApiClientException>();
    }

    [Fact]
    public async Task GetForecastAsync_WhenServerReturns404_ReturnsNull()
    {
        // 404 for forecast = no data yet (no accounts synced), NOT an error.
        var response = new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent("", Encoding.UTF8, "application/json")
        };

        var client = BuildClient(response);

        var result = await client.GetForecastAsync(months: 3);

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetForecastAsync_WhenServerReturns500_ThrowsApiClientException()
    {
        // 500 is a server failure — NOT "no data". Must throw, not return null.
        var response = new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("error", Encoding.UTF8, "text/plain")
        };

        var client = BuildClient(response);

        var act = () => client.GetForecastAsync(months: 3);

        await act.Should().ThrowAsync<ApiClientException>();
    }

    // ── ApiClientException carries inner exception ─────────────────────────────

    [Fact]
    public async Task GetAccountsAsync_WhenJsonMalformed_InnerExceptionIsJsonException()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<html>bad</html>", Encoding.UTF8, "application/json")
        };

        var client = BuildClient(response);

        var act = () => client.GetAccountsAsync();

        var exception = await act.Should().ThrowAsync<ApiClientException>();
        exception.Which.InnerException.Should().BeOfType<JsonException>();
    }

    // ── SyncResult shape tests ────────────────────────────────────────────────

    [Fact]
    public void SyncResult_DeserializesApiPropertyNames_Correctly()
    {
        // The API returns camelCase: accountsUpserted, transactionsAdded, transactionsSkipped.
        var json = """
            {
                "accountsUpserted": 3,
                "transactionsAdded": 42,
                "transactionsSkipped": 7
            }
            """;

        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var result = JsonSerializer.Deserialize<SyncResult>(json, options);

        result.Should().NotBeNull();
        result!.AccountsUpserted.Should().Be(3);
        result.TransactionsAdded.Should().Be(42);
        result.TransactionsSkipped.Should().Be(7);
    }

    [Fact]
    public void SyncResult_OldPropertyNames_DoNotDeserialize()
    {
        // The OLD (wrong) property names — AccountsSynced, TransactionsSynced, Status —
        // should produce zeros/null, NOT the real data, verifying the mismatch was fixed.
        var json = """
            {
                "accountsSynced": 3,
                "transactionsSynced": 42,
                "status": "Success"
            }
            """;

        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var result = JsonSerializer.Deserialize<SyncResult>(json, options);

        // SyncResult no longer has these properties — deserialization yields zeroes.
        result.Should().NotBeNull();
        result!.AccountsUpserted.Should().Be(0);
        result.TransactionsAdded.Should().Be(0);
        result.TransactionsSkipped.Should().Be(0);
    }

    [Fact]
    public async Task SyncAsync_WhenResponseHasApiContractShape_ReturnsSyncResultWithCorrectValues()
    {
        var json = """{"accountsUpserted":2,"transactionsAdded":18,"transactionsSkipped":1}""";
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        var client = BuildClient(response);

        var result = await client.SyncAsync();

        result.Should().NotBeNull();
        result!.AccountsUpserted.Should().Be(2);
        result.TransactionsAdded.Should().Be(18);
        result.TransactionsSkipped.Should().Be(1);
    }

    // ── Logger receives error on failure ──────────────────────────────────────

    [Fact]
    public async Task GetAccountsAsync_WhenJsonMalformed_LogsError()
    {
        var html = "<html><body>error</body></html>";
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(html, Encoding.UTF8, "application/json")
        };

        var logger = new SpyLogger<FlowLedgerApiClient>();
        var handler = new StubHttpMessageHandler(response);
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost") };
        var client = new FlowLedgerApiClient(http, logger);

        try
        {
            await client.GetAccountsAsync();
        }
        catch (ApiClientException) { }

        logger.ErrorCount.Should().BeGreaterThan(0, "the logger should have received at least one LogError call");
    }

    [Fact]
    public async Task GetAccountsAsync_WhenNonSuccessStatus_LogsError()
    {
        var response = new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("error", Encoding.UTF8, "text/plain")
        };

        var logger = new SpyLogger<FlowLedgerApiClient>();
        var handler = new StubHttpMessageHandler(response);
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost") };
        var client = new FlowLedgerApiClient(http, logger);

        try
        {
            await client.GetAccountsAsync();
        }
        catch (ApiClientException) { }

        logger.ErrorCount.Should().BeGreaterThan(0, "the logger should have received at least one LogError call");
    }
}

// ── Test doubles ──────────────────────────────────────────────────────────────

/// <summary>
/// HTTP message handler stub: always returns the pre-built response.
/// </summary>
internal sealed class StubHttpMessageHandler(HttpResponseMessage response) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
        => Task.FromResult(response);
}

/// <summary>
/// Counts how many <c>LogError</c> calls were made, for assertion purposes.
/// </summary>
internal sealed class SpyLogger<T> : ILogger<T>
{
    private int _errorCount;
    public int ErrorCount => _errorCount;

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (logLevel >= LogLevel.Error)
        {
            Interlocked.Increment(ref _errorCount);
        }
    }
}
