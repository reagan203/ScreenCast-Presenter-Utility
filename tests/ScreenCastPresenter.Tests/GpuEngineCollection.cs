using Xunit.Abstractions;

[assembly: TestCollectionOrderer("ScreenCastPresenter.Tests.GpuEngineFirst", "ScreenCastPresenter.Tests")]

namespace ScreenCastPresenter.Tests;

/// <summary>
/// The test that runs the real capture engine into a window. In the app every engine runs in its
/// own short-lived helper process; in a test process, Windows refuses a window swap chain once
/// other window and GPU tests have run (E_ACCESSDENIED, from state the graphics stack keeps for
/// the process). So this collection runs on its own, before the other window tests.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class GpuEngineCollection
{
    public const string Name = "Real capture engine";
}

/// <summary>Puts <see cref="GpuEngineCollection"/> first; the rest keep their usual order.</summary>
public sealed class GpuEngineFirst : ITestCollectionOrderer
{
    public IEnumerable<ITestCollection> OrderTestCollections(IEnumerable<ITestCollection> testCollections) =>
        testCollections.OrderBy(c => c.DisplayName.Contains(GpuEngineCollection.Name, StringComparison.Ordinal) ? 0 : 1);
}
