using Content.Android.Launcher.Models;

namespace Content.Android;

internal static class FobosBuild
{
    public const string Engine = "276.0.0";
    public const string ContentRevision = "94c8636c48eb4d5797a5037f00438917f925337b";
    public const string Fork = "dsfobos";

    public static bool Matches(ServerBuildInfo? build) => build != null
        && build.EngineVersion == Engine
        && build.Version == ContentRevision
        && build.ForkId == Fork;
}
