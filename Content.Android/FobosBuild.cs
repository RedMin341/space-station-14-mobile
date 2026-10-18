using Content.Android.Launcher.Models;

namespace Content.Android;

internal static class FobosBuild
{
    public const string Engine = "276.0.0";
    public const string ContentRevision = "ba798f9b51b0f9fbb7b9d56cea01ef7411451f0a";
    public const string Fork = "dsfobos";

    public static bool Matches(ServerBuildInfo? build) => build != null
        && build.EngineVersion == Engine
        && build.Version == ContentRevision
        && build.ForkId == Fork;
}
