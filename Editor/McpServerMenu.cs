using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Strodio.McpAddressables
{
    public static class McpServerMenu
    {
        const int MinPythonMajor = 3;
        const int MinPythonMinor = 10;

        static string PackageRoot => Path.GetFullPath("Packages/com.strodio.unity-mcp-addressables");
        static string McpDir => Path.Combine(PackageRoot, "MCP~");
        static string VenvDir => Path.Combine(McpDir, "venv");
        static string RequirementsFile => Path.Combine(McpDir, "requirements.txt");

        static string PythonExe
        {
            get
            {
#if UNITY_EDITOR_WIN
                return Path.Combine(VenvDir, "Scripts", "python.exe");
#else
                return Path.Combine(VenvDir, "bin", "python");
#endif
            }
        }

        static string PipExe
        {
            get
            {
#if UNITY_EDITOR_WIN
                return Path.Combine(VenvDir, "Scripts", "pip.exe");
#else
                return Path.Combine(VenvDir, "bin", "pip");
#endif
            }
        }

        static string ServerPy => Path.Combine(McpDir, "server.py");

        // ─── Server Controls ───

        [MenuItem("Tools/MCP Addressables/Start Server")]
        static void StartServer() => McpHttpServer.Start();

        [MenuItem("Tools/MCP Addressables/Start Server", true)]
        static bool ValidateStartServer() => !McpHttpServer.IsRunning;

        [MenuItem("Tools/MCP Addressables/Stop Server")]
        static void StopServer() => McpHttpServer.Stop();

        [MenuItem("Tools/MCP Addressables/Stop Server", true)]
        static bool ValidateStopServer() => McpHttpServer.IsRunning;

        // ─── Environment Setup ───

        [MenuItem("Tools/MCP Addressables/Setup Python Environment")]
        static void SetupEnvironment()
        {
            string pythonPath = FindSuitablePython(out string version);

            if (pythonPath == null)
            {
                EditorUtility.DisplayDialog("MCP Addressables",
                    $"Python {MinPythonMajor}.{MinPythonMinor}+ not found.\n\n" +
                    "The MCP SDK requires Python 3.10 or later.\n" +
                    "Please install it from: https://www.python.org/downloads/\n\n" +
                    "On macOS with Homebrew:\n  brew install python@3.12",
                    "OK");
                return;
            }

            Debug.Log($"[MCP Addressables] Using Python: {pythonPath} ({version})");

            bool venvExists = Directory.Exists(VenvDir) && File.Exists(PythonExe);

            if (venvExists)
            {
                bool venvOk = GetPythonVersion(PythonExe, out string venvVersion);
                string msg = venvOk
                    ? $"Python virtual environment already exists.\nVenv Python: {venvVersion}\n\nDo you want to reinstall dependencies?"
                    : "Python virtual environment exists but appears broken.\n\nRecreate it?";

                bool proceed = EditorUtility.DisplayDialog("MCP Addressables", msg, venvOk ? "Reinstall" : "Recreate", "Cancel");
                if (!proceed) return;

                if (!venvOk)
                {
                    Directory.Delete(VenvDir, true);
                }
                else
                {
                    InstallDependencies();
                    return;
                }
            }

            EditorUtility.DisplayProgressBar("MCP Addressables", $"Creating virtual environment with {version}...", 0.2f);
            bool createOk = RunCommand(pythonPath, $"-m venv \"{VenvDir}\"", out string createOutput);
            EditorUtility.ClearProgressBar();

            if (!createOk)
            {
                Debug.LogError($"[MCP Addressables] Failed to create venv: {createOutput}");
                EditorUtility.DisplayDialog("MCP Addressables",
                    $"Failed to create virtual environment.\n\n{createOutput}", "OK");
                return;
            }

            Debug.Log("[MCP Addressables] Virtual environment created.");
            UpgradePipAndInstall();
        }

        static void UpgradePipAndInstall()
        {
            EditorUtility.DisplayProgressBar("MCP Addressables", "Upgrading pip...", 0.4f);
            RunCommand(PythonExe, "-m pip install --upgrade pip", out string pipOutput);
            Debug.Log($"[MCP Addressables] pip upgrade: {pipOutput}");

            EditorUtility.ClearProgressBar();
            InstallDependencies();
        }

        static void InstallDependencies()
        {
            EditorUtility.DisplayProgressBar("MCP Addressables", "Installing Python dependencies (mcp, httpx)...", 0.6f);
            bool ok = RunCommand(PipExe, $"install -r \"{RequirementsFile}\"", out string output);
            EditorUtility.ClearProgressBar();

            if (ok)
            {
                Debug.Log($"[MCP Addressables] Dependencies installed successfully.\n{output}");
                EditorUtility.DisplayDialog("MCP Addressables",
                    "Environment setup complete!\n\n" +
                    "Next step: add MCP Server config to Claude Code.\n" +
                    "Use menu: Tools > MCP Addressables > Copy MCP Config",
                    "OK");
            }
            else
            {
                Debug.LogError($"[MCP Addressables] Failed to install dependencies: {output}");
                EditorUtility.DisplayDialog("MCP Addressables",
                    $"Failed to install dependencies.\n\n{output}", "OK");
            }
        }

        // ─── Status & Config ───

        [MenuItem("Tools/MCP Addressables/Check Environment")]
        static void CheckEnvironment()
        {
            string sysPython = FindSuitablePython(out string sysVersion);
            bool hasPython = sysPython != null;
            bool hasVenv = Directory.Exists(VenvDir) && File.Exists(PythonExe);
            string venvVersion = "";
            bool hasDeps = false;

            if (hasVenv)
            {
                GetPythonVersion(PythonExe, out venvVersion);
                hasDeps = RunCommand(PipExe, "show mcp", out _);
            }

            bool serverRunning = McpHttpServer.IsRunning;

            string status =
                $"System Python 3.10+:  {(hasPython ? $"OK ({sysVersion} at {sysPython})" : "NOT FOUND")}\n" +
                $"Virtual Env:          {(hasVenv ? $"OK ({venvVersion})" : "NOT CREATED")}\n" +
                $"Dependencies:         {(hasDeps ? "OK" : "NOT INSTALLED")}\n" +
                $"HTTP Server:          {(serverRunning ? "Running (port 8091)" : "Stopped")}\n" +
                $"\nVenv path: {VenvDir}";

            bool allGood = hasPython && hasVenv && hasDeps && serverRunning;

            if (allGood)
            {
                Debug.Log($"[MCP Addressables] All checks passed.\n{status}");
                EditorUtility.DisplayDialog("MCP Addressables - All OK", status, "OK");
            }
            else
            {
                Debug.LogWarning($"[MCP Addressables] Environment check:\n{status}");
                bool needsSetup = hasPython && (!hasVenv || !hasDeps);
                bool setup = EditorUtility.DisplayDialog("MCP Addressables - Environment Check", status,
                    needsSetup ? "Setup Now" : "OK",
                    needsSetup ? "Later" : "");
                if (setup && needsSetup)
                    SetupEnvironment();
            }
        }

        [MenuItem("Tools/MCP Addressables/Copy MCP Config")]
        static void CopyMcpConfig()
        {
            string config =
                "{\n" +
                "  \"mcpServers\": {\n" +
                "    \"unity-addressables\": {\n" +
                $"      \"command\": \"{EscapeJsonPath(PythonExe)}\",\n" +
                $"      \"args\": [\"{EscapeJsonPath(ServerPy)}\"]\n" +
                "    }\n" +
                "  }\n" +
                "}";

            EditorGUIUtility.systemCopyBuffer = config;
            Debug.Log($"[MCP Addressables] MCP config copied to clipboard:\n{config}");
            EditorUtility.DisplayDialog("MCP Addressables",
                "MCP Server config has been copied to clipboard.\n\n" +
                "Paste it into your .claude/settings.json file.\n\n" +
                "(Also logged to Console for reference)", "OK");
        }

        [MenuItem("Tools/MCP Addressables/Server Status")]
        static void ShowStatus()
        {
            string status = McpHttpServer.IsRunning ? "Running" : "Stopped";
            Debug.Log($"[MCP Addressables] Server status: {status}");
            EditorUtility.DisplayDialog("MCP Addressables Server",
                $"Server is {status}\nEndpoint: http://localhost:8091/", "OK");
        }

        // ─── Python Discovery ───

        static string FindSuitablePython(out string version)
        {
            string[] candidates;

#if UNITY_EDITOR_OSX
            candidates = new[]
            {
                "/opt/homebrew/bin/python3",
                "/usr/local/bin/python3",
                "/opt/homebrew/bin/python3.14",
                "/opt/homebrew/bin/python3.13",
                "/opt/homebrew/bin/python3.12",
                "/opt/homebrew/bin/python3.11",
                "/opt/homebrew/bin/python3.10",
                "/usr/local/bin/python3.14",
                "/usr/local/bin/python3.13",
                "/usr/local/bin/python3.12",
                "/usr/local/bin/python3.11",
                "/usr/local/bin/python3.10",
                "python3",
                "python",
            };
#elif UNITY_EDITOR_WIN
            candidates = new[]
            {
                "python3",
                "python",
                "py -3",
            };
#else
            candidates = new[]
            {
                "python3",
                "python3.14",
                "python3.13",
                "python3.12",
                "python3.11",
                "python3.10",
                "python",
            };
#endif

            foreach (string cmd in candidates)
            {
                if (!GetPythonVersion(cmd, out string ver))
                    continue;

                if (ParseVersionMeetsMinimum(ver))
                {
                    version = ver;
                    return cmd;
                }
            }

            version = null;
            return null;
        }

        static bool GetPythonVersion(string pythonCmd, out string version)
        {
            version = null;
            if (!RunCommand(pythonCmd, "--version", out string output))
                return false;

            var match = Regex.Match(output.Trim(), @"Python\s+(\d+\.\d+\.\d+)");
            if (!match.Success)
                return false;

            version = match.Groups[1].Value;
            return true;
        }

        static bool ParseVersionMeetsMinimum(string version)
        {
            var parts = version.Split('.');
            if (parts.Length < 2) return false;
            if (!int.TryParse(parts[0], out int major)) return false;
            if (!int.TryParse(parts[1], out int minor)) return false;
            return major > MinPythonMajor || (major == MinPythonMajor && minor >= MinPythonMinor);
        }

        // ─── Helpers ───

        static bool RunCommand(string command, string arguments, out string output)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = command,
                    Arguments = arguments,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };

                using (var process = Process.Start(psi))
                {
                    string stdout = process.StandardOutput.ReadToEnd();
                    string stderr = process.StandardError.ReadToEnd();
                    process.WaitForExit(600000); // 10 min – pip may download large wheels (cryptography ~7MB)

                    output = string.IsNullOrEmpty(stderr) ? stdout : $"{stdout}\n{stderr}".Trim();
                    return process.ExitCode == 0;
                }
            }
            catch (System.Exception e)
            {
                output = e.Message;
                return false;
            }
        }

        static string EscapeJsonPath(string path)
        {
            return path.Replace("\\", "/");
        }
    }
}
