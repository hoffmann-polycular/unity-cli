// MIT Copyright (c) 2025 DevBookOfArray
// See /LICENSE-MIT for the full MIT license text.



using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace UnityCliConnector.Tools
{
    [UnityCliTool(Name = "exec", Description = "Execute arbitrary C# code at runtime. Full access to Unity and all loaded assemblies.")]
    public static class ExecuteCsharp
    {
        private static readonly string[] DefaultUsings =
        {
            "System",
            "System.Collections.Generic",
            "System.IO",
            "System.Linq",
            "System.Reflection",
            "System.Threading.Tasks",
            "UnityEngine",
            "UnityEngine.SceneManagement",
            "UnityEditor",
            "UnityEditor.SceneManagement",
            "UnityEditorInternal",
        };

        public class Parameters
        {
            [ToolParameter("C# code to execute. Use 'return' for output.", Required = true)]
            public string Code { get; set; }

            [ToolParameter("Additional using directives (comma-separated, e.g. Unity.Entities,Unity.Mathematics)")]
            public string[] Usings { get; set; }

            [ToolParameter("Path to csc compiler (csc.dll or csc.exe). Auto-detected if omitted.")]
            public string Csc { get; set; }

            [ToolParameter("Path to dotnet runtime. Auto-detected if omitted.")]
            public string Dotnet { get; set; }
        }

        public static object HandleCommand(JObject parameters)
        {
            var p = new ToolParams(parameters);
            var code = p.Get("code")
                ?? (p.GetRaw("args") as JArray)?[0]?.ToString();
            if (string.IsNullOrEmpty(code))
                return new ErrorResponse("'code' required");

            var usingsToken = p.GetRaw("usings");
            var extraUsings = new List<string>();
            if (usingsToken != null)
            {
                if (usingsToken.Type == JTokenType.Array)
                    extraUsings.AddRange(usingsToken.ToObject<string[]>());
                else
                    extraUsings.AddRange(usingsToken.ToString().Split(','));
            }

            var cscPath = p.Get("csc");
            var dotnetPath = p.Get("dotnet");

            var source = BuildSource(code, extraUsings);
            if (source.Error != null)
                return ErrorResponse.Usage(source.Error);

            return CompileAndExecute(source, cscPath, dotnetPath);
        }

        /// <summary>
        /// A generated compilation unit plus the mapping needed to report its
        /// compile errors against the snippet the caller actually wrote.
        /// </summary>
        private class GeneratedSource
        {
            public string Text;
            /// <summary>Generated-file line (1-based) → snippet line (1-based).</summary>
            public Dictionary<int, int> LineMap = new Dictionary<int, int>();
            /// <summary>Set when the snippet cannot be wrapped at all.</summary>
            public string Error;
        }

        /// <summary>
        /// A using directive: `using X;`, `using static X.Y;` or
        /// `using Alias = X.Y;`. Deliberately does not match `using (...)`
        /// statements or C# 8 `using var x = ...;` declarations, both of which
        /// are legal inside the method body.
        /// </summary>
        private static readonly Regex UsingDirective = new Regex(
            @"\G\s*using\s+(static\s+)?[A-Za-z_@][\w.]*\s*(=\s*[^;\n]+)?;",
            RegexOptions.Compiled);

        /// <summary>
        /// A using directive occupying a whole line on its own. Used to spot the
        /// ones HoistUsings cannot lift (they follow other code), so they can be
        /// rejected with an explanation instead of a bare syntax error.
        /// </summary>
        private static readonly Regex StrandedUsing = new Regex(
            @"^[ \t]*using\s+(static\s+)?[A-Za-z_@][\w.]*\s*(=\s*[^;\n]+)?;[ \t]*$",
            RegexOptions.Compiled | RegexOptions.Multiline);

        private static GeneratedSource BuildSource(string code, List<string> extraUsings)
        {
            var hoistedCode = HoistUsings(code.Replace("\r\n", "\n"), out var hoisted);

            var stranded = StrandedUsing.Match(hoistedCode);
            if (stranded.Success)
            {
                var strandedLine = 1;
                for (var i = 0; i < stranded.Index; i++)
                    if (hoistedCode[i] == '\n') strandedLine++;
                return new GeneratedSource
                {
                    Error = $"L{strandedLine}: '{stranded.Value.Trim()}' — using directives are only " +
                            "lifted out of an exec snippet when they come before any other code. " +
                            "Move it to the top of the snippet, or pass it via --usings.",
                };
            }

            var body = hoistedCode.Split('\n');
            var result = new GeneratedSource();
            var sb = new StringBuilder();
            var line = 0;

            foreach (var u in DefaultUsings)
            {
                sb.AppendLine($"using {u};");
                line++;
            }
            foreach (var u in extraUsings)
            {
                var trimmed = u.Trim();
                if (trimmed.Length == 0) continue;
                sb.AppendLine($"using {trimmed};");
                line++;
            }
            foreach (var h in hoisted)
            {
                sb.AppendLine(h.Key);
                result.LineMap[++line] = h.Value;
            }

            sb.AppendLine();
            sb.AppendLine("public static class __CliDynamic {");
            sb.AppendLine("  public static object Execute() {");
            line += 3;

            for (var i = 0; i < body.Length; i++)
            {
                sb.AppendLine(body[i]);
                result.LineMap[++line] = i + 1;
            }

            sb.AppendLine("  }");
            sb.AppendLine("}");

            result.Text = sb.ToString();
            return result;
        }

        /// <summary>
        /// A snippet is spliced into a method body, where `using` directives are
        /// illegal — so lift the ones the caller wrote to the front of the
        /// generated file instead of failing on them. Only directives at the
        /// very start of the snippet are lifted (`using X; return X.Y();` and
        /// the multi-line form both qualify); each is blanked out in place, so
        /// every remaining character keeps its original line.
        /// </summary>
        /// <param name="code">Snippet with newlines already normalised to \n.</param>
        /// <param name="hoisted">Directive text paired with its snippet line.</param>
        private static string HoistUsings(string code, out List<KeyValuePair<string, int>> hoisted)
        {
            hoisted = new List<KeyValuePair<string, int>>();

            var body = code.ToCharArray();
            var at = 0;
            while (at < code.Length)
            {
                var m = UsingDirective.Match(code, at);
                if (!m.Success) break;

                var line = 1;
                for (var i = 0; i < m.Index; i++)
                    if (code[i] == '\n') line++;

                hoisted.Add(new KeyValuePair<string, int>(m.Value.Trim(), line));
                for (var i = m.Index; i < m.Index + m.Length; i++)
                    if (body[i] != '\n') body[i] = ' ';

                at = m.Index + m.Length;
            }

            return new string(body);
        }

        private static object CompileAndExecute(GeneratedSource source, string cscOverride = null, string dotnetOverride = null)
        {
            var utf8 = new UTF8Encoding(false);
            var tmpDir = Path.Combine(Path.GetTempPath(), "unity-cli-exec");
            Directory.CreateDirectory(tmpDir);

            var id = Guid.NewGuid().ToString("N").Substring(0, 8);
            var srcFile = Path.Combine(tmpDir, $"{id}.cs");
            var outFile = Path.Combine(tmpDir, $"{id}.dll");
            var rspFile = Path.Combine(tmpDir, $"{id}.rsp");

            try
            {
                File.WriteAllText(srcFile, source.Text, utf8);

                var rsp = new StringBuilder();
                rsp.AppendLine("-target:library");
                rsp.AppendLine($"-out:\"{outFile}\"");
                rsp.AppendLine("-nologo");
                rsp.AppendLine("-nowarn:0105,1701,1702");
                rsp.AppendLine("-langversion:latest");
                rsp.AppendLine($"\"{srcFile}\"");

                var added = new HashSet<string>();
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try
                    {
                        if (asm.IsDynamic || string.IsNullOrEmpty(asm.Location)) continue;
                        if (!added.Add(asm.GetName().Name)) continue;
                        rsp.AppendLine($"-r:\"{asm.Location}\"");
                    }
                    catch { }
                }

                File.WriteAllText(rspFile, rsp.ToString(), utf8);

                var rspArg = $"@\"{rspFile}\"";
                var csc = FindCsc(cscOverride);
                string exe, args;

                if (csc != null && csc.EndsWith(".dll"))
                {
                    var dotnet = FindDotnet(dotnetOverride);
                    if (dotnet == null)
                        return new ErrorResponse(
                            "Cannot find dotnet runtime under: " +
                            EditorApplication.applicationContentsPath +
                            "\nSpecify the path manually with --dotnet <path>");
                    exe = dotnet;
                    args = $"exec \"{csc}\" {rspArg}";
                }
                else if (csc != null)
                {
                    exe = csc;
                    args = rspArg;
                }
                else
                {
                    return new ErrorResponse(
                        "Cannot find csc compiler under: " +
                        EditorApplication.applicationContentsPath +
                        "\nSpecify the path manually with --csc <path-to-csc.dll-or-csc.exe>");
                }

                var psi = new ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = args,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                };

                using (var proc = Process.Start(psi))
                {
                    var stdout = proc.StandardOutput.ReadToEnd();
                    var stderr = proc.StandardError.ReadToEnd();
                    proc.WaitForExit(30000);

                    if (proc.ExitCode != 0)
                    {
                        var output = string.IsNullOrEmpty(stderr) ? stdout : stderr;
                        return new ErrorResponse($"Compile error:\n{FormatErrors(output, source.LineMap)}");
                    }
                }

                var bytes = File.ReadAllBytes(outFile);
                var compiled = Assembly.Load(bytes);
                var method = compiled.GetType("__CliDynamic")?.GetMethod("Execute");
                if (method == null)
                    return new ErrorResponse("Internal error: compiled type or method not found.");

                object result;
                try
                {
                    result = method.Invoke(null, null);
                }
                catch (TargetInvocationException tie)
                {
                    var inner = tie.InnerException ?? tie;
                    return new ErrorResponse($"Runtime error: {inner.GetType().Name}: {inner.Message}");
                }
                return new SuccessResponse("OK", Serialize(result, 0));
            }
            finally
            {
                try { File.Delete(srcFile); } catch { }
                try { File.Delete(outFile); } catch { }
                try { File.Delete(rspFile); } catch { }
            }
        }

        private static string FindCsc(string cscOverride = null)
        {
            if (!string.IsNullOrEmpty(cscOverride))
                return cscOverride;

            var content = EditorApplication.applicationContentsPath;
            var cscDll = SearchFile(content, "csc.dll");
            if (cscDll != null) return cscDll;

            if (Application.platform == RuntimePlatform.WindowsEditor)
            {
                var cscExe = SearchFile(content, "csc.exe");
                if (cscExe != null) return cscExe;
            }

            return null;
        }

        private static string SearchFile(string dir, string name)
        {
            try
            {
                var files = Directory.GetFiles(dir, name, SearchOption.AllDirectories);
                foreach (var f in files)
                    if (Path.GetFileName(f) == name)
                        return f;
            }
            catch { }
            return null;
        }

        private static string FindDotnet(string dotnetOverride = null)
        {
            if (!string.IsNullOrEmpty(dotnetOverride))
                return dotnetOverride;

            var name = "dotnet" + (Application.platform == RuntimePlatform.WindowsEditor ? ".exe" : "");
            var found = SearchFile(EditorApplication.applicationContentsPath, name);
            if (found != null) return found;

            if (Application.platform != RuntimePlatform.WindowsEditor)
            {
                var macPaths = new[]
                {
                    "/usr/local/share/dotnet/dotnet",
                    "/opt/homebrew/bin/dotnet",
                    "/usr/local/bin/dotnet",
                };
                foreach (var p in macPaths)
                    if (File.Exists(p)) return p;
            }

            return name;
        }

        /// <summary>
        /// Rewrites csc's diagnostics to positions in the caller's snippet.
        /// Lines belonging to the generated wrapper have no snippet position,
        /// so they are reported without a misleading line number.
        /// </summary>
        private static string FormatErrors(string raw, Dictionary<int, int> lineMap)
        {
            var lines = raw.Split('\n');
            var errors = new List<string>();
            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (string.IsNullOrEmpty(trimmed)) continue;
                var m = Regex.Match(trimmed, @"\((\d+),\d+\):\s*error\s+\w+:\s*(.+)");
                if (m.Success)
                {
                    var message = m.Groups[2].Value;
                    int generatedLine;
                    int snippetLine;
                    if (int.TryParse(m.Groups[1].Value, out generatedLine)
                        && lineMap != null
                        && lineMap.TryGetValue(generatedLine, out snippetLine))
                        errors.Add($"L{snippetLine}: {message}");
                    else
                        errors.Add(message);
                }
                else if (trimmed.Contains("error"))
                {
                    errors.Add(trimmed);
                }
            }
            return errors.Count > 0 ? string.Join("\n", errors) : raw;
        }

        private static object Serialize(object obj, int depth)
        {
            if (obj == null) return null;
            if (depth > 4) return obj.ToString();
            var type = obj.GetType();
            if (type.IsPrimitive || type == typeof(string) || type == typeof(decimal)) return obj;
            if (type.IsEnum) return obj.ToString();
            if (type.Name.StartsWith("FixedString")) return obj.ToString();
            if (obj is IDictionary dict)
            {
                var r = new Dictionary<string, object>();
                foreach (DictionaryEntry e in dict)
                    r[e.Key.ToString()] = Serialize(e.Value, depth + 1);
                return r;
            }
            if (obj is IEnumerable enumerable)
            {
                var list = new List<object>();
                int count = 0;
                foreach (var item in enumerable)
                {
                    if (count++ >= 100) { list.Add("... (truncated at 100)"); break; }
                    list.Add(Serialize(item, depth + 1));
                }
                return list;
            }
            if (type.IsValueType || type.IsClass)
            {
                var fields = type.GetFields(BindingFlags.Public | BindingFlags.Instance);
                if (fields.Length > 0)
                {
                    var r = new Dictionary<string, object>();
                    foreach (var f in fields)
                    {
                        try { r[f.Name] = Serialize(f.GetValue(obj), depth + 1); }
                        catch { r[f.Name] = "<error>"; }
                    }
                    return r;
                }
            }
            return obj.ToString();
        }
    }
}
