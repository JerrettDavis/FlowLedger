using FlowLedger.Application.Abstractions;
using FlowLedger.Application.Features.Query;
using FlowLedger.Application.Tests.Fakes;
using FlowLedger.Domain.Aggregates;
using FlowLedger.Domain.ValueObjects;
using FluentAssertions;

namespace FlowLedger.Application.Tests.Query;

public sealed class AskQueryTests
{
    private sealed class NoAssets : IAssetRepository
    {
        public Task<Asset?> GetByIdAsync(AssetId id, CancellationToken ct = default) => Task.FromResult<Asset?>(null);
        public Task<IReadOnlyList<Asset>> ListAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Asset>>([]);
        public Task AddAsync(Asset asset, CancellationToken ct = default) => Task.CompletedTask;
        public Task SaveChangesAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class NoHistory : IBalanceHistoryReader
    {
        public Task<IReadOnlyList<BalancePoint>> DailyAccountBalancesAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<BalancePoint>>([]);
    }

    private sealed class ScriptedTranslator(params string[] replies) : IQueryTranslator
    {
        private int _i;
        public List<IReadOnlyList<(string Role, string Content)>> Seen { get; } = [];
        public string? LastSystem { get; private set; }

        public Task<string> CompleteAsync(string system, IReadOnlyList<(string Role, string Content)> conversation, CancellationToken ct = default)
        {
            LastSystem = system;
            Seen.Add(conversation.ToList());
            return Task.FromResult(replies[Math.Min(_i++, replies.Length - 1)]);
        }
    }

    private sealed class DownTranslator : IQueryTranslator
    {
        public Task<string> CompleteAsync(string system, IReadOnlyList<(string Role, string Content)> conversation, CancellationToken ct = default)
            => throw new HttpRequestException("connection refused");
    }

    private static AskQueryHandler Handler(IQueryTranslator t) =>
        new(t, new QueryDatasetProvider(new FakeTransactionRepository(), new FakeAccountRepository(), new FakeCategoryRepository(), new NoAssets(), new NoHistory()));

    private const string Fence = "```";

    [Fact]
    public async Task Valid_query_is_returned_after_one_attempt_and_fences_are_stripped()
    {
        var reply = "<think>hmm</think>\n" + Fence + "\ntransactions | summarize total = sum(amount)\n" + Fence;
        var t = new ScriptedTranslator(reply);

        var r = await Handler(t).HandleAsync("how much have I spent?");

        r.Error.Should().BeNull();
        r.Attempts.Should().Be(1);
        r.Query.Should().Be("transactions | summarize total = sum(amount)");
    }

    [Fact]
    public async Task Bad_query_is_repaired_by_feeding_the_error_back_to_the_model()
    {
        var t = new ScriptedTranslator("transactions | where nonsense_col == 1", "transactions | count");

        var r = await Handler(t).HandleAsync("how many transactions?");

        r.Error.Should().BeNull();
        r.Attempts.Should().Be(2);
        r.Query.Should().Be("transactions | count");
        t.Seen[1].Last().Content.Should().Contain("failed");
    }

    [Fact]
    public async Task Gives_up_after_max_attempts_with_a_friendly_error()
    {
        var t = new ScriptedTranslator("this is not a query |||");

        var r = await Handler(t).HandleAsync("???");

        r.Query.Should().NotBeNull();
        r.Error.Should().Contain("rephras");
        r.Attempts.Should().Be(AskQueryHandler.MaxAttempts);
    }

    [Fact]
    public async Task Filter_that_matches_nothing_gets_one_repair_turn_then_a_visible_note()
    {
        var t = new ScriptedTranslator("transactions | where category == \"Nope\" | count", "transactions | where category contains \"nope\" | count");

        var r = await Handler(t).HandleAsync("how many?");

        r.Error.Should().BeNull();
        r.Note.Should().Contain("matched no rows");
        t.Seen.Should().HaveCount(2);
        t.Seen[1].Last().Content.Should().Contain("matched no rows");
    }

    [Fact]
    public async Task Unfiltered_empty_result_is_a_normal_answer()
    {
        var t = new ScriptedTranslator("transactions | count");

        var r = await Handler(t).HandleAsync("how many?");

        r.Note.Should().BeNull();
        r.Attempts.Should().Be(1);
    }

    [Fact]
    public async Task Slow_model_is_reported_as_loading_not_unreachable()
    {
        var r = await Handler(new SlowTranslator()).HandleAsync("anything");

        r.Query.Should().BeNull();
        r.Error.Should().Contain("loading");
    }

    private sealed class SlowTranslator : IQueryTranslator
    {
        public Task<string> CompleteAsync(string system, IReadOnlyList<(string Role, string Content)> conversation, CancellationToken ct = default)
            => throw new TaskCanceledException("timed out");
    }

    [Fact]
    public async Task Unreachable_model_gives_an_actionable_message()
    {
        var r = await Handler(new DownTranslator()).HandleAsync("anything");

        r.Query.Should().BeNull();
        r.Error.Should().Contain("Ollama");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Blank_question_is_rejected_without_calling_the_model(string q)
    {
        var t = new ScriptedTranslator("transactions | count");

        var r = await Handler(t).HandleAsync(q);

        r.Error.Should().NotBeNull();
        t.Seen.Should().BeEmpty();
    }

    [Fact]
    public async Task Prompt_carries_schema_and_todays_date()
    {
        var t = new ScriptedTranslator("transactions | count");
        await Handler(t).HandleAsync("count");

        t.LastSystem.Should().Contain("Dataset `transactions`").And.Contain("direction").And.Contain(DateTime.UtcNow.Year.ToString());
    }
}
