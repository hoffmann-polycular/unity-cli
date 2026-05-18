// MIT Copyright (c) 2025 DevBookOfArray
// See /LICENSE-MIT for the full MIT license text.



using System;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.Compilation;

namespace UnityCliConnector.Tools
{
    [UnityCliTool(Description = "Refresh Unity assets and optionally request script compilation.")]
    public static class RefreshUnity
    {
        public class Parameters
        {
            [ToolParameter("Refresh mode: if_dirty (default) or force")]
            public string Mode { get; set; }

            [ToolParameter("Scope: all (default) or specific path")]
            public string Scope { get; set; }

            [ToolParameter("Compile mode: none (default) or request")]
            public string Compile { get; set; }
        }

        public static object HandleCommand(JObject @params)
        {
            var p = new ToolParams(@params ?? new JObject());
            string mode = p.Get("mode", "if_dirty");
            string scope = p.Get("scope", "all");
            string compile = p.Get("compile", "none");

            bool compileRequested = false;

            AssetDatabase.Refresh(string.Equals(mode, "force", StringComparison.OrdinalIgnoreCase)
                ? ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport
                : ImportAssetOptions.ForceSynchronousImport);

            if (string.Equals(compile, "request", StringComparison.OrdinalIgnoreCase))
            {
                Heartbeat.MarkCompileRequested();
                CompilationPipeline.RequestScriptCompilation();
                compileRequested = true;
            }

            return new SuccessResponse("Refresh requested.", new
            {
                refresh_triggered = true,
                compile_requested = compileRequested,
            });
        }
    }
}
