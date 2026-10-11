using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

public static class GenericVideoBuilder
{
    public static void BuildBatch()
    {
        if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.Android)
            throw new BuildFailedException("Start Unity with -buildTarget Android");
        string root = Directory.GetParent(Application.dataPath).FullName;
        string output = Environment.GetEnvironmentVariable("XRBT_VIDEO_APK") ??
            Path.Combine(root, "Builds", "XRoboToolkit-GenericVideo.apk");
        string oldId = PlayerSettings.GetApplicationIdentifier(BuildTargetGroup.Android);
        string oldName = PlayerSettings.productName;
        bool oldKeystore = PlayerSettings.Android.useCustomKeystore;
        var oldArchitectures = PlayerSettings.Android.targetArchitectures;
        var oldTargetSdk = PlayerSettings.Android.targetSdkVersion;
        var oldBackend = PlayerSettings.GetScriptingBackend(BuildTargetGroup.Android);
        bool oldBundle = EditorUserBuildSettings.buildAppBundle;
        try
        {
            // Side-by-side installation preserves the deployed APK and its settings.
            PlayerSettings.SetApplicationIdentifier(BuildTargetGroup.Android, "com.xrobotoolkit.client.genericvideo");
            PlayerSettings.productName = "XRoboToolkit Video";
            PlayerSettings.Android.useCustomKeystore = false;
            PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
            PlayerSettings.Android.targetSdkVersion = (AndroidSdkVersions)31;
            PlayerSettings.SetScriptingBackend(BuildTargetGroup.Android, ScriptingImplementation.IL2CPP);
            EditorUserBuildSettings.buildAppBundle = false;
            Directory.CreateDirectory(Path.GetDirectoryName(output));
            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions {
                scenes = EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path).ToArray(),
                target = BuildTarget.Android, locationPathName = output, options = BuildOptions.None
            });
            if (report.summary.result != BuildResult.Succeeded)
                throw new BuildFailedException("Generic video APK build: " + report.summary.result);
            Debug.Log("GENERIC_VIDEO_APK=" + output);
        }
        finally
        {
            PlayerSettings.SetApplicationIdentifier(BuildTargetGroup.Android, oldId);
            PlayerSettings.productName = oldName;
            PlayerSettings.Android.useCustomKeystore = oldKeystore;
            PlayerSettings.Android.targetArchitectures = oldArchitectures;
            PlayerSettings.Android.targetSdkVersion = oldTargetSdk;
            PlayerSettings.SetScriptingBackend(BuildTargetGroup.Android, oldBackend);
            EditorUserBuildSettings.buildAppBundle = oldBundle;
            AssetDatabase.SaveAssets();
        }
    }
}
