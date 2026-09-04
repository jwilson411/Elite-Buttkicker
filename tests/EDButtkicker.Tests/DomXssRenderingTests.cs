using System.ComponentModel;
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
        var scriptDirectory = LocateScriptDirectory();
        EnsureDependenciesInstalled(scriptDirectory);

        var startInfo = new ProcessStartInfo
        {
            FileName = "node",
            WorkingDirectory = scriptDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add(ScriptName);

        using var process = Start(startInfo);

        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();

        Assert.True(process.ExitCode == 0, $"{ScriptName} failed:\n{output}\n{error}");
    }

    /// <summary>
    /// node_modules is not in the repository, so on a clean checkout - CI's, most of the time - the
    /// packages the script needs have to be fetched first.
    /// </summary>
    private static void EnsureDependenciesInstalled(string scriptDirectory)
    {
        if (Directory.Exists(Path.Combine(scriptDirectory, "node_modules", "jsdom")))
        {
            return;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = "npm",
            WorkingDirectory = scriptDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add("install");

        using var install = Start(startInfo, "npm");

        var output = install.StandardOutput.ReadToEnd();
        var error = install.StandardError.ReadToEnd();
        install.WaitForExit();

        Assert.True(install.ExitCode == 0, $"npm install in {scriptDirectory} failed:\n{output}\n{error}");
    }

    private static Process Start(ProcessStartInfo startInfo, string executable = "node")
    {
        try
        {
            return Process.Start(startInfo)!;
        }
        catch (Win32Exception)
        {
            Assert.Fail($"{executable} is required to run the DOM escaping tests but was not found on PATH.");
            throw;
        }
    }

    /// <summary>Walks out of bin/Debug/net8.0 to the test project's own js directory.</summary>
    private static string LocateScriptDirectory()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "js");
            if (File.Exists(Path.Combine(candidate, ScriptName)))
            {
                return candidate;
            }
        }

        Assert.Fail($"Could not find js/{ScriptName} above {AppContext.BaseDirectory}");
        throw new InvalidOperationException();
    }
}
