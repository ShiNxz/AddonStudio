using AddonStudio.Core.Contracts;
using AddonStudio.Core.Util;

namespace AddonStudio.Tests.Support;

/// <summary>An environment with fixed answers, for host tests that must not depend on the machine.</summary>
public sealed class FakeEnvironment : IEnvironmentService
{
    public bool Compile { get; set; }

    public bool Publish { get; set; }

    public int Invalidations { get; private set; }

    public Os Os => Platform.Os;

    public Task<DoctorReport> DoctorAsync(CancellationToken ct = default)
    {
        return Task.FromResult(new DoctorReport(Os, Compile, Publish, null,
            [new DoctorCheck(DoctorCheckId.Os, "Windows", Os == Os.Windows ? CheckStatus.Ok : CheckStatus.Error, "fake", null)]));
    }

    public string? Cs2Path() => null;

    public string? CompilerPath() => null;

    public string? GameInfoPath() => null;

    public string RequireCs2Path() => throw new AddonStudioException(ErrorCodes.Cs2Missing, "no CS2 here");

    public string RequireCompilerPath() => throw new AddonStudioException(ErrorCodes.Cs2Missing, "no CS2 here");

    public bool CanCompile() => Compile;

    public bool CanPublish() => Publish;

    public void Invalidate() => Invalidations++;
}

/// <summary>A build that emits build.started and then waits until it is cancelled.</summary>
public sealed class WaitingBuild : IBuildService
{
    public Task<BuildPlan> PlanAsync(string path, CancellationToken ct = default) => throw new AddonStudioException(ErrorCodes.NotFound, "no plan");

    public async Task<BuildResult> RunAsync(string path, BuildMode mode, IReadOnlyList<string>? files, IEventSink events, CancellationToken ct = default)
    {
        events.Emit(EventNames.BuildStarted, new BuildStartedEvent(path, 3));
        await Task.Delay(Timeout.Infinite, ct);

        return new BuildResult(true, false, 0, [], 0, []);
    }

    public Task<CleanResult> CleanAsync(string path, CancellationToken ct = default) => Task.FromResult(new CleanResult(4));
}
