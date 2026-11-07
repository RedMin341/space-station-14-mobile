using Content.Android.Launcher.Models;

namespace Content.Android;

internal static class FobosBuild
{
    public const string Engine = "276.0.6";
    public const string ContentRevision = "28e2afab9ed20a3af35b177eaef318dae1e81fff";
    public const string Fork = "dsfobos";

    // Content can change independently of the engine; download it from Fobos /info.
    // Rebuilding the APK is required only when the engine (or fork) changes.
    public static bool Matches(ServerBuildInfo? build) => build != null
        && build.EngineVersion == Engine
        && build.ForkId == Fork;
}
