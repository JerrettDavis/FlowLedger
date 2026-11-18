using FlowLedger.Application.Features.Categories;
using FlowLedger.Application.Tests.Fakes;
using FlowLedger.Domain.Aggregates;
using FlowLedger.Domain.ValueObjects;
using FluentAssertions;

namespace FlowLedger.Application.Tests.Categories;

public sealed class CategoryRestructureServiceTests
{
    private sealed class FakeStore : ICategoryMaintenanceStore
    {
        public CategoryRestructurePlan? Applied { get; private set; }

        public Task<int> ApplyAsync(CategoryRestructurePlan plan, CancellationToken ct = default)
        {
            Applied = plan;
            return Task.FromResult(7);
        }
    }

    private readonly FakeTenantContext _tenant = new();
    private readonly FakeCategoryRepository _repo = new();
    private readonly FakeStore _store = new();

    private async Task<Category> AddAsync(string path, bool system = false)
    {
        var tenantId = TenantId.From(_tenant.TenantId);
        var cat = system
            ? new Category(CategoryId.New(), tenantId, new CategoryPath(path), path.Split('/').Last(), isSystem: true)
            : Category.Create(tenantId, new CategoryPath(path), path.Split('/').Last());
        await _repo.AddAsync(cat);
        return cat;
    }

    private CategoryRestructureService Service() => new(_repo, _store);

    [Fact]
    public async Task Renaming_moves_the_whole_subtree()
    {
        var food = await AddAsync("Food");
        await AddAsync("Food/Dining");

        var plan = await Service().PlanAsync(food.Id, "Meals");

        plan.Merges.Should().BeEmpty();
        plan.Moves.Select(m => (m.OldPath, m.NewPath)).Should().BeEquivalentTo(
            [("Food", "Meals"), ("Food/Dining", "Meals/Dining")]);
        plan.Moves.Single(m => m.OldPath == "Food").DisplayName.Should().Be("Meals");
    }

    [Fact]
    public async Task Moving_under_another_parent_relinks_the_parent()
    {
        var dining = await AddAsync("Food/Dining");
        var life = await AddAsync("Life");

        var plan = await Service().PlanAsync(dining.Id, "Life/Dining");

        var move = plan.Moves.Single();
        move.NewPath.Should().Be("Life/Dining");
        move.ParentId.Should().Be(life.Id);
    }

    [Fact]
    public async Task Pointing_at_an_existing_path_merges_and_recurses_through_children()
    {
        var eating = await AddAsync("Eating Out");
        var food = await AddAsync("Food");
        await AddAsync("Food/Dining");
        var diningTarget = await AddAsync("Eating Out/Dining");
        await AddAsync("Food/Snacks");

        var plan = await Service().PlanAsync(food.Id, "Eating Out");

        plan.Merges.Select(m => (m.OldPath, m.TargetPath)).Should().BeEquivalentTo(
            [("Food", "Eating Out"), ("Food/Dining", "Eating Out/Dining")]);
        plan.Merges.Single(m => m.OldPath == "Food").TargetId.Should().Be(eating.Id);
        plan.Merges.Single(m => m.OldPath == "Food/Dining").TargetId.Should().Be(diningTarget.Id);
        plan.Moves.Should().ContainSingle(m => m.OldPath == "Food/Snacks" && m.NewPath == "Eating Out/Snacks" && m.ParentId == eating.Id);
    }

    [Fact]
    public async Task Cannot_move_into_itself_or_to_the_same_path_or_blank()
    {
        var food = await AddAsync("Food");

        await FluentActions.Awaiting(() => Service().PlanAsync(food.Id, "Food/Sub/Deeper")).Should().ThrowAsync<ArgumentException>();
        await FluentActions.Awaiting(() => Service().PlanAsync(food.Id, "Food")).Should().ThrowAsync<ArgumentException>();
        await FluentActions.Awaiting(() => Service().PlanAsync(food.Id, " / ")).Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task System_categories_cannot_be_moved_but_can_be_merge_targets()
    {
        var sys = await AddAsync("Transfer", system: true);
        var mine = await AddAsync("Misc");

        await FluentActions.Awaiting(() => Service().PlanAsync(sys.Id, "Other")).Should().ThrowAsync<InvalidOperationException>();

        var plan = await Service().PlanAsync(mine.Id, "Transfer");
        plan.Merges.Should().ContainSingle(m => m.TargetId == sys.Id);
    }

    [Fact]
    public async Task Dry_run_applies_nothing_and_real_run_does()
    {
        var food = await AddAsync("Food");

        var preview = await Service().MoveAsync(food.Id, "Meals", dryRun: true);
        _store.Applied.Should().BeNull();
        preview.Moved.Should().Be(1);

        var result = await Service().MoveAsync(food.Id, "Meals", dryRun: false);
        _store.Applied.Should().NotBeNull();
        result.TransactionsAffected.Should().Be(7);
    }

    [Fact]
    public async Task Unknown_category_is_not_found()
    {
        await FluentActions.Awaiting(() => Service().PlanAsync(Guid.NewGuid(), "X")).Should().ThrowAsync<KeyNotFoundException>();
    }
}
