using Celeste.Mod;

namespace Celeste.Mod.CelesteAutoCut;

public class CelesteAutoCutSettings : EverestModuleSettings {
    public static CelesteAutoCutSettings Instance { get; private set; } = null!;

    public CelesteAutoCutSettings() {
        Instance = this;
    }

    public string OutputDirectory { get; set; } = "";
}
