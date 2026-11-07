using Content.Android.Launcher.Models;

namespace Content.Android;

internal static class FobosBuild
{
    public const string Engine = "276.0.6";
    public const string ContentRevision = "28e2afab9ed20a3af35b177eaef318dae1e81fff";
    public const string Fork = "dsfobos";

    public static bool Matches(ServerBuildInfo? build) => build != null
        && build.EngineVersion == Engine
        && build.Version == ContentRevision
        && build.ForkId == Fork;
}
