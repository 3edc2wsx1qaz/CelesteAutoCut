using Celeste.Mod;

namespace Celeste.Mod.CelesteAutoCut;

public enum ClipIntensitySetting {
    Low,
    High
}

public class CelesteAutoCutSettings : EverestModuleSettings {
    public static CelesteAutoCutSettings Instance { get; private set; } = null!;

    public CelesteAutoCutSettings() {
        Instance = this;
    }

    public string OutputDirectory { get; set; } = "";

    public ClipIntensitySetting ClipIntensity { get; set; } = ClipIntensitySetting.Low;

    public bool LogOutputEnabled { get; set; }
}
