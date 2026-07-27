using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

public static class XRoboToolkitBatchBuild
{
    public static void BuildAndroid()
    {
        string outputPath = Path.GetFullPath(GetRequiredArgument("-buildOutput"));
        string[] scenes = EditorBuildSettings.scenes
            .Where(scene => scene.enabled)
            .Select(scene => scene.path)
            .ToArray();

        if (scenes.Length == 0)
        {
            throw new BuildFailedException("No enabled scenes are configured for the Android build.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath));

        bool originalCustomKeystore = PlayerSettings.Android.useCustomKeystore;
        bool originalBuildAppBundle = EditorUserBuildSettings.buildAppBundle;
        try
        {
            // The upstream project contains a developer-specific Windows keystore path.
            // Use Unity's debug keystore so unattended builds remain reproducible.
            PlayerSettings.Android.useCustomKeystore = false;
            EditorUserBuildSettings.buildAppBundle = false;

            BuildReport report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = scenes,
                locationPathName = outputPath,
                target = BuildTarget.Android,
                options = BuildOptions.None
            });

            BuildSummary summary = report.summary;
            if (summary.result != BuildResult.Succeeded)
            {
                throw new BuildFailedException(
                    $"Android build failed: {summary.result}, errors={summary.totalErrors}");
            }

            Debug.Log($"XRoboToolkit Android APK: {outputPath} ({summary.totalSize} bytes)");
        }
        finally
        {
            PlayerSettings.Android.useCustomKeystore = originalCustomKeystore;
            EditorUserBuildSettings.buildAppBundle = originalBuildAppBundle;
        }
    }

    private static string GetRequiredArgument(string name)
    {
        string[] args = Environment.GetCommandLineArgs();
        for (int i = 0; i + 1 < args.Length; i++)
        {
            if (args[i] == name && !string.IsNullOrWhiteSpace(args[i + 1]))
            {
                return args[i + 1];
            }
        }

        throw new BuildFailedException($"Missing required command line argument: {name}");
    }
}
