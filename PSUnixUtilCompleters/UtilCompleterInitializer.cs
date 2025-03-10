using System;
using System.Collections.Generic;
using System.IO;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace PSUnixUtilCompleters
{
    public class UtilCompleterInitializer : IModuleAssemblyInitializer
    {
        private enum ShellType
        {
            None = 0,
            Zsh,
            Bash,
        }

        private const string SHELL_PREFERENCE_VARNAME = "COMPLETION_SHELL_PREFERENCE";

        private readonly static IReadOnlyDictionary<string, ShellType> s_shells = new Dictionary<string, ShellType>()
        {
            { "zsh", ShellType.Zsh },
            { "bash", ShellType.Bash },
        };

        private readonly static PropertyInfo s_executionContext = typeof(Runspace).GetProperty("ExecutionContext", BindingFlags.NonPublic | BindingFlags.Instance);

        private readonly static PropertyInfo s_nativeArgumentCompleters = s_executionContext.PropertyType.GetProperty("NativeArgumentCompleters", BindingFlags.NonPublic | BindingFlags.Instance);

        public void OnImport()
        {
            string preferredCompletionShell = Environment.GetEnvironmentVariable(SHELL_PREFERENCE_VARNAME);

            ShellType shellType;
            string shellExePath;
            if ((string.IsNullOrEmpty(preferredCompletionShell) || !TryFindShell(preferredCompletionShell, out shellExePath, out shellType))
                && !TryFindFallbackShell(out shellExePath, out shellType))
            {
                WriteError("Unable to find shell to provide unix utility completions");
                return;
            }

            IUnixUtilCompleter utilCompleter;
            switch (shellType)
            {
                case ShellType.Bash:
                    utilCompleter = new BashUtilCompleter(shellExePath);
                    break;

                case ShellType.Zsh:
                    utilCompleter = new ZshUtilCompleter(shellExePath);
                    break;

                default:
                    WriteError("Unable to find shell to provide unix utility completions");
                    return;
            }

            IEnumerable<string> utilsToComplete = utilCompleter.FindCompletableCommands();

            UnixUtilCompletion.SetCompleter(utilCompleter);

            RegisterCompletersForCommands(utilsToComplete);
        }

        private void RegisterCompletersForCommands(IEnumerable<string> commands)
        {
            object executionContext = s_executionContext.GetValue(Runspace.DefaultRunspace);

            var nativeArgumentCompleters = (Dictionary<string, ScriptBlock>)s_nativeArgumentCompleters.GetValue(executionContext);
            if (nativeArgumentCompleters == null)
            {
                s_nativeArgumentCompleters.SetValue(executionContext, new Dictionary<string, ScriptBlock>());
                nativeArgumentCompleters = (Dictionary<string, ScriptBlock>)s_nativeArgumentCompleters.GetValue(executionContext);
            }

            foreach (string command in commands)
            {
                nativeArgumentCompleters[command] = UnixUtilCompletion.CreateInvocationScriptBlock(command);
            }
        }

        private bool TryFindShell(string shellName, out string shellPath, out ShellType shellType)
        {
            // No shell name provided
            if (string.IsNullOrEmpty(shellName))
            {
                shellPath = null;
                shellType = ShellType.None;
                return false;
            }

            // Look for absolute path to a shell
            if (Path.IsPathRooted(shellName)
                && s_shells.TryGetValue(Path.GetFileName(shellName), out shellType)
                && File.Exists(shellName))
            {
                shellPath = shellName;
                return true;
            }

            // Now assume the shell is just a command name, and confirm we recognize it
            if (!s_shells.TryGetValue(shellName, out shellType))
            {
                shellPath = null;
                return false;
            }

            return TryFindShellByName(shellName, out shellPath);
        }

        private bool TryFindFallbackShell(out string foundShell, out ShellType shellType)
        {
            foreach (KeyValuePair<string, ShellType> shell in s_shells)
            {
                if (TryFindShellByName(shell.Key, out foundShell))
                {
                    shellType = shell.Value;
                    return true;
                }
            }

            foundShell = null;
            shellType = ShellType.None;
            return false;
        }

        private bool TryFindShellByName(string shellName, out string foundShellPath)
        {
            foreach (string utilDir in UnixHelpers.NativeUtilDirs)
            {
                string shellPath = Path.Combine(utilDir, shellName);
                if (File.Exists(shellPath))
                {
                    foundShellPath = shellPath;
                    return true;
                }
            }

            foundShellPath = null;
            return false;
        }

        private void WriteError(string errorMessage)
        {
            using (var pwsh = PowerShell.Create())
            {
                pwsh.AddCommand("Write-Error")
                    .AddParameter("Message", errorMessage)
                    .Invoke();
            }
        }
    }
}
