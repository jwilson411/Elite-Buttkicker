using System.Diagnostics;
using Xunit;

namespace EDButtkicker.Tests;

/// <summary>
/// The browser half of the escaping story cannot be exercised from .NET, so it lives in
/// js/dom_xss_test.js: it loads the real js/dom.js into a DOM and feeds markup payloads through the
/// helpers every renderer uses. This runs that script under node and fails with its output.
/// </summary>
public class DomXssRenderingTests
{
    private const string ScriptName = "dom_xss_test.js";

    [Fact]
    public void UntrustedText_IsNeverParsedAsMarkup()
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
