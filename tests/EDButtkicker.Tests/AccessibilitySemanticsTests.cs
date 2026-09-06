using System.Diagnostics;
using Xunit;

namespace EDButtkicker.Tests;

/// <summary>
/// Keyboard and screen-reader semantics live in wwwroot markup and js/app.js. This runs
/// js/a11y_test.js under node (jsdom) and fails with its output. It cannot substitute for NVDA.
/// </summary>
public class AccessibilitySemanticsTests
{
    private const string ScriptName = "a11y_test.js";

    [Fact]
    public void TabsDialogsAndToasts_ExposeAccessibleSemantics()
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
