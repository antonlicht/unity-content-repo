using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace ContentRepo.Editor
{
    /// <summary>
    /// Runs a batch-mode entry point's async work and exits Unity when it is done. The work awaits background tasks
    /// whose continuations run on the main thread, so blocking the main thread until it finishes would never return.
    /// Instead the entry point returns right away, the editor keeps updating, and the exit code is set once the work
    /// completes: 0 when it succeeded, 1 when it failed or reported a failure.
    /// </summary>
    internal static class ContentCli
    {
        public static void Run(string name, Func<Task<bool>> work)
        {
            if (Environment.GetCommandLineArgs().Any(arg => string.Equals(arg, "-quit", StringComparison.OrdinalIgnoreCase)))
            {
                Debug.LogError($"[{name}] Remove -quit from the command line. This command finishes after the method returns and " +
                               "exits Unity itself; with -quit Unity would quit before the work is done.");
                EditorApplication.Exit(1);
                return;
            }

            Task<bool> task;
            try { task = work(); }
            catch (Exception ex)
            {
                Debug.LogError($"[{name}] {ex}");
                EditorApplication.Exit(1);
                return;
            }

            void AbortOnReload()
            {
                Debug.LogError($"[{name}] Scripts were reloaded before the command finished, so it cannot complete.");
                EditorApplication.Exit(1);
            }

            void Poll()
            {
                if (!task.IsCompleted) return;
                EditorApplication.update -= Poll;
                AssemblyReloadEvents.beforeAssemblyReload -= AbortOnReload;

                if (task.IsFaulted || task.IsCanceled)
                {
                    Debug.LogError($"[{name}] {(task.IsCanceled ? "Cancelled." : task.Exception?.GetBaseException().ToString())}");
                    EditorApplication.Exit(1);
                    return;
                }
                if (!task.Result) Debug.LogError($"[{name}] Finished with failures.");
                else Debug.Log($"[{name}] Done.");
                EditorApplication.Exit(task.Result ? 0 : 1);
            }

            AssemblyReloadEvents.beforeAssemblyReload += AbortOnReload;
            EditorApplication.update += Poll;
        }

        /// <summary>
        /// Fails when the content repository is missing, is not a sparse checkout, or has no package checked out, so a
        /// command that works on every checked-out package cannot report success after doing nothing.
        /// </summary>
        public static async Task RequireCheckedOutPackagesAsync()
        {
            var root = ContentGitApi.ContentAbsolutePath;
            if (!Directory.Exists(Path.Combine(root, ".git")) && !File.Exists(Path.Combine(root, ".git")))
                throw new InvalidOperationException(
                    $"No content repository at '{root}'. Initialize it in the Content Browser or clone it there first.");
            try { await ContentGitApi.RunGitCommandAsync("sparse-checkout list", root); }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"The content repository at '{root}' is not a sparse checkout, so its checked-out packages cannot be listed. {ex.Message}");
            }
            if ((await ContentGitApi.GetCheckedOutFoldersAsync()).Count == 0)
                throw new InvalidOperationException($"No content packages are checked out in '{root}'.");
        }
    }
}
