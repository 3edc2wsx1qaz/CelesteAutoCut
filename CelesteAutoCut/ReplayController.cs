using System.IO;
using Celeste.Mod;

namespace Celeste.Mod.CelesteAutoCut;

internal sealed class ReplayController {
    private const string ReplayFolder = "CelesteAutoCutReplays";

    public string ReplayDirectory => Path.Combine(Everest.PathGame, ReplayFolder);

    public void Stop() {
        // Legacy input replay recording/playback has been removed; this remains
        // as a no-op lifecycle hook for module unload compatibility.
    }
}
