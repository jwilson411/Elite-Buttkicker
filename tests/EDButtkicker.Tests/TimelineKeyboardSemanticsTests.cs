using System.Diagnostics;
using Xunit;

namespace EDButtkicker.Tests;

/// <summary>
/// The timeline editor's keyboard path - adding, selecting, editing and deleting control points and
/// layers without a mouse - lives in js/timeline-editor.js. This runs js/timeline_keyboard_test.js
/// under node (jsdom) and fails with its output. It cannot substitute for NVDA.
/// </summary>
public class TimelineKeyboardSemanticsTests
{
    private const string ScriptName = "timeline_keyboard_test.js";

    [Fact]
    public void TimelinePointsAndLayers_AreEditableFromTheKeyboard()
    {
        var scriptDirectory = NodeJsdomTestSupport.LocateScriptDirectory(ScriptName);
        NodeJsdomTestSupport.EnsureDependenciesInstalled(scriptDirectory);

        var startInfo = new ProcessStartInfo
        {
            FileName = "node",
            WorkingDirectory = scriptDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add(ScriptName);

        using var process = NodeJsdomTestSupport.Start(startInfo, "node");

        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();

        Assert.True(process.ExitCode == 0, $"{ScriptName} failed:\n{output}\n{error}");
    }
}
