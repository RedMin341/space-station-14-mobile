using Content.Android.Launcher.Models;

namespace Content.Android;

internal static class FobosBuild
{
    public const string Engine = "276.0.0";
    public const string ContentRevision = "91d2edc0e9deaa4bdf733d313fde646ecef7ed3d";
    public const string Fork = "dsfobos";

    public static bool Matches(ServerBuildInfo? build) => build != null
        && build.EngineVersion == Engine
        && build.Version == ContentRevision
        && build.ForkId == Fork;
}
