using System.Diagnostics;
using Xunit;

namespace EDButtkicker.Tests;

/// <summary>
/// Runs the shipped setup wizard and its event handlers against deferred responses,
/// including navigation, dismissal and failed confirmations.
/// </summary>
public class SetupNavigationTests
{
    private const string ScriptName = "setup_navigation_test.js";

    [Fact]
    public void SetupNavigation_DiscardsObsoleteResponses()
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
