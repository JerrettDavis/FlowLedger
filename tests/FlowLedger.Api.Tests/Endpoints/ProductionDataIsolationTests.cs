using System.Net;
using System.Text.Json;
using FlowLedger.Infrastructure.DataSafety;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace FlowLedger.Api.Tests.Endpoints;

/// <summary>#64: in production mode the simulated (demo) connector can never put rows into the household's data.</summary>
[Collection("ApiIntegration")]
public sealed class ProductionDataIsolationTests(FlowLedgerApiFactory factory) : IAsyncLifetime
{
    public Task InitializeAsync() => factory.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private static void SetEnvironment(DataEnvironmentState state, DataEnvironment? value) =>
        typeof(DataEnvironmentState).GetProperty(nameof(DataEnvironmentState.Current))!.SetValue(state, value);

    [Fact]
    public async Task Production_mode_refuses_the_demo_connect_and_queries_return_no_demo_rows()
    {
        var state = factory.Services.GetRequiredService<DataEnvironmentState>();
        var before = state.Current;
        SetEnvironment(state, DataEnvironment.Production);
        try
        {
            var client = factory.CreateAuthenticatedClient();

            (await client.PostAsync("/api/connect", null)).StatusCode.Should().Be(HttpStatusCode.Conflict);

            using var accounts = JsonDocument.Parse(await client.GetStringAsync("/api/accounts"));
            accounts.RootElement.GetArrayLength().Should().Be(0, "no simulated accounts may exist in production data");
            using var transactions = JsonDocument.Parse(await client.GetStringAsync("/api/transactions"));
            (transactions.RootElement.ValueKind == JsonValueKind.Array ? transactions.RootElement.GetArrayLength() : transactions.RootElement.GetProperty("items").GetArrayLength())
                .Should().Be(0);
        }
        finally
        {
            SetEnvironment(state, before);
        }
    }
}
