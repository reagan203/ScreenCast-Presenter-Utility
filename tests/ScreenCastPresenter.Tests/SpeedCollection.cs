namespace ScreenCastPresenter.Tests;

/// <summary>
/// Tests that measure how fast something is run on their own, never beside other tests: a busy
/// CPU would make the numbers meaningless (a slow machine still passes; a real slowdown doesn't).
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SpeedCollection
{
    public const string Name = "Speed budgets";
}
