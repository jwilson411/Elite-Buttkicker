using System.ComponentModel;
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
            Assert.Fail($"{executable} is required to run the accessibility tests but was not found on PATH.");
            throw;
        }
    }

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
