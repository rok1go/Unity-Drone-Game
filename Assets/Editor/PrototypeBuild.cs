using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

public static class PrototypeBuild
{
    public const string MainScene = "Assets/Scenes/Main/Main.unity";
    public const string OutputPath = "Builds/MultiplayerPrototype/DroneHideAndSeek.exe";
    private static bool queued;

    public static void Queue()
    {
        if (queued || BuildPipeline.isBuildingPlayer) return;
        queued = true;
        EditorApplication.delayCall -= Build;
        EditorApplication.update += RunQueuedBuild;
    }

    private static void RunQueuedBuild()
    {
        EditorApplication.update -= RunQueuedBuild;
        queued = false;
        Build();
    }

    [MenuItem("Drone Game/Build multiplayer prototype")]
    public static void Build()
    {
        if (BuildPipeline.isBuildingPlayer) return;
        string project = Path.GetDirectoryName(Application.dataPath);
        string output = Path.Combine(project, OutputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(output));
        File.WriteAllText(Path.Combine(project, "Builds/MultiplayerPrototype/build-result.txt"), "Building");
        var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
        {
            scenes = new[] { MainScene }, locationPathName = output,
            target = BuildTarget.StandaloneWindows64, options = BuildOptions.Development
        });
        string result = report.summary.result + "; errors=" + report.summary.totalErrors +
            "; size=" + report.summary.totalSize + "; duration=" + report.summary.totalTime;
        File.WriteAllText(Path.Combine(project, "Builds/MultiplayerPrototype/build-result.txt"), result);
        if (report.summary.result != BuildResult.Succeeded) Debug.LogError("Prototype build: " + result);
        else Debug.Log("Prototype build: " + result);
    }
}
