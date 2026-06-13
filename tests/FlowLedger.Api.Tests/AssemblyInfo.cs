// Disable cross-collection parallelization. The Development ("ApiIntegration") and
// Production ("ProductionApi") WebApplicationFactory instances target the same Program
// entry point; running them in parallel can trip the shared host-builder cache and
// cause spurious ObjectDisposedException during host start. Tests within a collection
// already run sequentially.
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]
