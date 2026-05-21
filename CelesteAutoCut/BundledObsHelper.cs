using System;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using Celeste.Mod;

namespace Celeste.Mod.CelesteAutoCut;

internal static class BundledObsHelper {
    private const string PayloadResourceName = "CelesteAutoCut.ObsClipPanelPayload.zip";
    private const string VersionMarkerFileName = ".celesteautocut-helper-version";

    public static string ResolveHelperPath(string configuredPath) {
        if (string.IsNullOrWhiteSpace(configuredPath)) {
            configuredPath = @"ObsClipPanel\ObsClipPanel.exe";
        }

        if (Path.IsPathRooted(configuredPath)) {
            return Path.GetFullPath(configuredPath);
        }

        string relativePath = configuredPath
            .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
            .Replace('\\', Path.DirectorySeparatorChar);

        string extractedPath = Path.GetFullPath(Path.Combine(Everest.PathGame, "CelesteAutoCutTools", relativePath));
        EnsureBundledHelperExtracted(extractedPath);
        if (File.Exists(extractedPath)) {
            return extractedPath;
        }

        string currentFolderInstallPath = Path.GetFullPath(Path.Combine(Everest.PathGame, "Mods", "CelesteAutoCut", "bin", relativePath));
        if (File.Exists(currentFolderInstallPath)) {
            return currentFolderInstallPath;
        }

        string legacyFolderInstallPath = Path.GetFullPath(Path.Combine(Everest.PathGame, "Mods", "CelesteReplay", "bin", relativePath));
        return File.Exists(legacyFolderInstallPath) ? legacyFolderInstallPath : extractedPath;
    }

    private static void EnsureBundledHelperExtracted(string helperExecutablePath) {
        string helperDir = Path.GetDirectoryName(helperExecutablePath)
            ?? throw new InvalidOperationException("Failed to resolve OBS helper directory.");

        if (!NeedsExtraction(helperExecutablePath, helperDir)) {
            return;
        }

        using Stream payloadStream = typeof(BundledObsHelper).Assembly.GetManifestResourceStream(PayloadResourceName)
            ?? throw new FileNotFoundException($"Embedded OBS helper payload not found: {PayloadResourceName}");

        string tempDir = helperDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + ".tmp-" + Guid.NewGuid().ToString("N");
        string tempZipPath = tempDir + ".zip";

        try {
            EnsureDirectoryDeleted(tempDir);
            Directory.CreateDirectory(tempDir);

            using (FileStream file = File.Create(tempZipPath)) {
                payloadStream.CopyTo(file);
            }

            ZipFile.ExtractToDirectory(tempZipPath, tempDir);
            File.Delete(tempZipPath);

            File.WriteAllText(Path.Combine(tempDir, VersionMarkerFileName), GetPayloadVersion());

            string? parentDir = Path.GetDirectoryName(helperDir);
            if (!string.IsNullOrWhiteSpace(parentDir)) {
                Directory.CreateDirectory(parentDir);
            }

            EnsureDirectoryDeleted(helperDir);
            Directory.Move(tempDir, helperDir);
        } catch {
            EnsureDirectoryDeleted(tempDir);
            throw;
        } finally {
            TryDeleteFile(tempZipPath);
        }
    }

    private static bool NeedsExtraction(string helperExecutablePath, string helperDir) {
        string markerPath = Path.Combine(helperDir, VersionMarkerFileName);
        if (!File.Exists(helperExecutablePath) || !File.Exists(markerPath)) {
            return true;
        }

        string extractedVersion = File.ReadAllText(markerPath).Trim();
        return !string.Equals(extractedVersion, GetPayloadVersion(), StringComparison.Ordinal);
    }

    private static string GetPayloadVersion() {
        AssemblyName assemblyName = typeof(BundledObsHelper).Assembly.GetName();
        using Stream payloadStream = typeof(BundledObsHelper).Assembly.GetManifestResourceStream(PayloadResourceName)
            ?? throw new FileNotFoundException($"Embedded OBS helper payload not found: {PayloadResourceName}");
        using var sha256 = SHA256.Create();
        string assemblyVersion = assemblyName.Version?.ToString() ?? "0.0.0.0";
        string payloadHash = Convert.ToHexString(sha256.ComputeHash(payloadStream));
        return $"{assemblyVersion}:{payloadHash}";
    }

    private static void EnsureDirectoryDeleted(string path) {
        if (!Directory.Exists(path)) {
            return;
        }

        foreach (string file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)) {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(path, recursive: true);
    }

    private static void TryDeleteFile(string path) {
        try {
            if (File.Exists(path)) {
                File.SetAttributes(path, FileAttributes.Normal);
                File.Delete(path);
            }
        } catch {
            // best effort only
        }
    }
}

