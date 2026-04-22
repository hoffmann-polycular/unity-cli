// unity-cli - Control the Unity Editor from the command line.
// Copyright (C) 2026  Tobias Hoffmann Polycular GmbH
// 
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
// 
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
// 
// You should have received a copy of the GNU General Public License
// along with this program.  If not, see <https://www.gnu.org/licenses/>.
// 
// COMMERCIAL LICENSE NOTICE:
// If you wish to use this code inside a non-GPL, proprietary software product, 
// you must instead acquire a commercial license from the copyright holder.
// 
// Contact: info@polycular.com | Website: https://www.polycular.com/



using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace UnityCliConnector.Tools
{
    /// <summary>
    /// Lists scene hierarchy: scene roots when given no path, or the children
    /// of a specific GameObject when given a path. Renders as human-readable
    /// tree, structured JSON, plain (one path per line), or null-delimited.
    /// </summary>
    [UnityCliTool(Name = "ls",
        Description = "List scene hierarchy. No path lists scene roots; with a path lists children.")]
    public static class Ls
    {
        public class Parameters
        {
            [ToolParameter("GameObject path to list children of. Omit for scene roots.")]
            public string Path { get; set; }

            [ToolParameter("Recurse into descendants (-r / --recursive on the CLI).")]
            public bool Recursive { get; set; }

            [ToolParameter("Include each object's component type list.")]
            public bool Components { get; set; }

            [ToolParameter("Output format: human (default), json, plain, null.")]
            public string Format { get; set; }
        }

        public static object HandleCommand(JObject @params)
        {
            var p = new ToolParams(@params);
            var path = p.Get("path")
                       ?? (p.GetRaw("args") as JArray)?[0]?.ToString();
            var recursive = p.GetBool("recursive");
            var includeComponents = p.GetBool("components");
            var format = (p.Get("format") ?? "human").ToLowerInvariant();

            List<GameObject> children;
            string rootPath;

            if (string.IsNullOrWhiteSpace(path))
            {
                children = PathResolver.GetSceneRoots();
                rootPath = "";
            }
            else
            {
                var parseResult = PathParser.Parse(path);
                if (!parseResult.IsSuccess) return new ErrorResponse(parseResult.ErrorMessage);

                var resolveResult = PathResolver.ResolveGameObject(parseResult.Value);
                if (!resolveResult.IsSuccess) return new ErrorResponse(resolveResult.ErrorMessage);

                var parent = resolveResult.Value;
                children = PathResolver.GetImmediateChildren(parent);
                rootPath = PathResolver.GetCanonicalPath(parent);
            }

            switch (format)
            {
                case "json":
                    return new SuccessResponse("", new
                    {
                        path = rootPath,
                        children = BuildJsonChildren(children, recursive, includeComponents),
                    });
                case "plain":
                    return new SuccessResponse("", RenderDelimited(children, recursive, "\n"));
                case "null":
                case "null-delimited":
                case "null_delimited":
                    return new SuccessResponse("", RenderDelimited(children, recursive, "\0"));
                case "human":
                case "":
                    return new SuccessResponse("", RenderHuman(rootPath, children, recursive, includeComponents));
                default:
                    return new ErrorResponse($"Unknown format '{format}'. Use: human, json, plain, null.");
            }
        }

        // ---- JSON rendering ----

        private static List<object> BuildJsonChildren(
            List<GameObject> children, bool recursive, bool includeComponents)
        {
            var list = new List<object>(children.Count);
            foreach (var c in children)
            {
                if (c == null) continue;
                var entry = new Dictionary<string, object>
                {
                    ["name"] = c.name,
                    ["path"] = PathResolver.GetCanonicalPath(c),
                    ["active"] = c.activeInHierarchy,
                };
                if (includeComponents)
                    entry["components"] = ComponentNames(c);
                if (recursive)
                {
                    var kids = PathResolver.GetImmediateChildren(c);
                    if (kids.Count > 0)
                        entry["children"] = BuildJsonChildren(kids, true, includeComponents);
                }
                list.Add(entry);
            }
            return list;
        }

        // ---- Plain / null-delimited rendering ----

        private static string RenderDelimited(List<GameObject> children, bool recursive, string sep)
        {
            var sb = new StringBuilder();
            var first = true;
            AppendDelimited(children, recursive, sb, sep, ref first);
            return sb.ToString();
        }

        private static void AppendDelimited(
            List<GameObject> children, bool recursive, StringBuilder sb, string sep, ref bool first)
        {
            foreach (var c in children)
            {
                if (c == null) continue;
                if (!first) sb.Append(sep);
                first = false;
                sb.Append(PathResolver.GetCanonicalPath(c));
                if (recursive)
                {
                    var kids = PathResolver.GetImmediateChildren(c);
                    if (kids.Count > 0)
                        AppendDelimited(kids, true, sb, sep, ref first);
                }
            }
        }

        // ---- Human rendering ----

        private static string RenderHuman(
            string rootPath, List<GameObject> children, bool recursive, bool includeComponents)
        {
            var sb = new StringBuilder();
            if (!string.IsNullOrEmpty(rootPath))
                sb.Append(rootPath).Append('\n');

            if (children.Count == 0)
            {
                sb.Append(string.IsNullOrEmpty(rootPath) ? "(empty scene)" : "  (no children)");
                return sb.ToString();
            }

            AppendHuman(children, recursive, includeComponents, sb, depth: 0);
            return sb.ToString().TrimEnd('\n');
        }

        private static void AppendHuman(
            List<GameObject> children, bool recursive, bool includeComponents,
            StringBuilder sb, int depth)
        {
            foreach (var c in children)
            {
                if (c == null) continue;
                sb.Append(' ', depth * 2);
                sb.Append(PathResolver.GetSegmentName(c));
                if (!c.activeInHierarchy) sb.Append("  (inactive)");
                if (includeComponents)
                    sb.Append("  [").Append(string.Join(", ", ComponentNames(c))).Append(']');
                sb.Append('\n');

                if (recursive)
                {
                    var kids = PathResolver.GetImmediateChildren(c);
                    if (kids.Count > 0)
                        AppendHuman(kids, true, includeComponents, sb, depth + 1);
                }
            }
        }

        private static List<string> ComponentNames(GameObject go)
        {
            var comps = go.GetComponents<Component>();
            var names = new List<string>(comps.Length);
            foreach (var comp in comps)
                names.Add(comp == null ? "<missing script>" : comp.GetType().Name);
            return names;
        }
    }
}
