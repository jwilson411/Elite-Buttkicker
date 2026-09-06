using System.ComponentModel;
using System.Diagnostics;
using Xunit;

namespace EDButtkicker.Tests;

/// <summary>
/// Three node/jsdom tests share one <c>tests/.../js</c> directory. xUnit runs them in parallel, so
/// a naive "if jsdom is missing, npm install" races on a clean CI checkout and one of them then
/// loads a half-written <c>node_modules</c>. One lock serialises the install; everyone else waits
/// and then sees the finished tree.
/// </summary>
internal static class NodeJsdomTestSupport
{
    private static readonly object InstallLock = new();

    public static void EnsureDependenciesInstalled(string scriptDirectory)
    {
        lock (InstallLock)
        {
            if (JsdomIsPresent(scriptDirectory))
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

            Assert.True(
                install.ExitCode == 0 && JsdomIsPresent(scriptDirectory),
                $"npm install in {scriptDirectory} failed:\n{output}\n{error}");
        }
    }

    public static Process Start(ProcessStartInfo startInfo, string executable)
    {
        try
        {
            return Process.Start(startInfo)!;
        }
        catch (Win32Exception)
        {
            Assert.Fail($"{executable} is required to run the node/jsdom tests but was not found on PATH.");
            throw;
        }
    }

    public static string LocateScriptDirectory(string scriptName)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "js");
            if (File.Exists(Path.Combine(candidate, scriptName)))
            {
                return candidate;
            }
        }

        Assert.Fail($"Could not find js/{scriptName} above {AppContext.BaseDirectory}");
        throw new InvalidOperationException();
    }

    private static bool JsdomIsPresent(string scriptDirectory) =>
        Directory.Exists(Path.Combine(scriptDirectory, "node_modules", "jsdom"));
}
