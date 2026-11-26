namespace ScreenCastPresenter.Tests;

/// <summary>
/// Tests that create real top-level windows run on their own, never alongside other tests. Even a
/// hidden window takes a place in Windows' stacking order, so one created by a parallel test could
/// land between the windows a stacking test is checking.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class DesktopWindowsCollection
{
    public const string Name = "Real desktop windows";
}
