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



using System;
using System.Collections.Generic;

namespace UnityCliConnector
{
    /// <summary>
    /// Top-level kind of a parsed path. The kind determines which backend
    /// resolves it (scene/prefab hierarchy, asset DB, project settings, or
    /// Unity instance ID).
    ///
    /// See unity-cli-path-contract-v3.md for the full grammar.
    /// </summary>
    public enum PathKind
    {
        /// <summary>GameObject under the Hierarchy or under the current selection.</summary>
        Scene,
        /// <summary>Unity instance ID form (e.g. "#14352").</summary>
        InstanceId,
        /// <summary>Project asset under "Assets/" or "Packages/".</summary>
        Asset,
        /// <summary>Project setting under "ProjectSettings/".</summary>
        ProjectSettings,
    }

    /// <summary>
    /// Where the segments of a Scene path are anchored.
    /// </summary>
    public enum PathAnchor
    {
        /// <summary>
        /// Bare, "./" or "../"+ — segments walk down from the current
        /// selection (after <see cref="ParsedPath.ParentJumps"/> walk-up
        /// steps). Fan-out across multiple selected objects is the default.
        /// </summary>
        Selection,
        /// <summary>
        /// "/" — segments walk down from the Hierarchy root (every loaded
        /// scene's roots, or the open prefab stage's root). Single absolute
        /// target; never fans out.
        /// </summary>
        Hierarchy,
    }

    /// <summary>
    /// One hierarchy segment (e.g. "Enemy" or "Enemy[1]").
    /// Index disambiguates duplicate sibling names; null means "unique or take first".
    /// </summary>
    public struct PathSegment
    {
        public string Name;
        public int? Index;

        public override string ToString()
        {
            return Index.HasValue ? $"{Name}[{Index.Value}]" : Name;
        }
    }

    /// <summary>
    /// Component reference on a GameObject (e.g. "Transform" or "AudioSource[0]").
    /// Index disambiguates multiple components of the same type.
    /// </summary>
    public struct ComponentRef
    {
        public string TypeName;
        public int? Index;

        public bool IsPresent => !string.IsNullOrEmpty(TypeName);

        public override string ToString()
        {
            if (!IsPresent) return "";
            return Index.HasValue ? $"{TypeName}[{Index.Value}]" : TypeName;
        }
    }

    /// <summary>
    /// Structured representation of a parsed path. Syntactic only — all
    /// reference resolution against live Unity state is the responsibility
    /// of <see cref="PathResolver"/>.
    /// </summary>
    public class ParsedPath
    {
        public PathKind Kind;

        // --- Kind == Scene ---

        /// <summary>Whether segments anchor at the selection or at the Hierarchy root.</summary>
        public PathAnchor Anchor;

        /// <summary>
        /// Number of "../" steps consumed before <see cref="Segments"/> begin.
        /// Only meaningful when <see cref="Anchor"/> is <see cref="PathAnchor.Selection"/>.
        /// 0 means "the selection itself (or its children)"; 1 means "the
        /// selection's parent (or that parent's children)"; etc.
        /// </summary>
        public int ParentJumps;

        // --- Kind == InstanceId ---

        public int InstanceId;

        // --- Kind == Asset ---

        /// <summary>Asset path on disk (everything before the "//" sub-asset separator).</summary>
        public string AssetPath;
        /// <summary>Sub-asset path inside the asset file (everything after "//"), empty if none.</summary>
        public string InnerPath;

        // --- Kind == ProjectSettings ---

        /// <summary>
        /// First segment after "ProjectSettings/" — the settings group name
        /// (e.g. "Physics", "Player", "QualitySettings"). Required.
        /// </summary>
        public string SettingsGroup;

        // Hierarchy segments. Used by Kind == Scene, Kind == Asset (when
        // InnerPath is non-empty), and Kind == ProjectSettings (drilling
        // into nested objects under a settings group, currently rare).
        public List<PathSegment> Segments = new();
        public ComponentRef Component;
        public List<string> Properties = new();

        /// <summary>Original user input — kept for error messages.</summary>
        public string Raw;
    }

    /// <summary>
    /// Parses user-supplied path strings into a <see cref="ParsedPath"/>.
    /// This is purely syntactic — it does NOT touch Unity state. Resolution
    /// (finding actual GameObjects / Components) is <see cref="PathResolver"/>.
    ///
    /// Recognized prefixes (in order):
    ///   #&lt;n&gt;                 → InstanceId
    ///   Assets/...           → Asset
    ///   Packages/...         → Asset
    ///   ProjectSettings/...  → ProjectSettings
    ///   /...                 → Scene (Hierarchy anchor)
    ///   ./...                → Scene (Selection anchor, 0 walk-up)
    ///   ../... [../...]      → Scene (Selection anchor, N walk-up)
    ///   .                    → Scene (Selection anchor, 0 walk-up, no segments)
    ///   ..                   → Scene (Selection anchor, 1 walk-up, no segments)
    ///   :Component[.prop]    → Scene (Selection anchor, no segments — operates on selection itself)
    ///   anything else        → Scene (Selection anchor, segments walk down from selection)
    /// </summary>
    public static class PathParser
    {
        public static Result<ParsedPath> Parse(string path)
        {
            if (path == null)
                return Result<ParsedPath>.Error("Path is null.");
            var trimmed = path.Trim();
            if (trimmed.Length == 0)
                return Result<ParsedPath>.Error("Path is empty.");

            var parsed = new ParsedPath { Raw = trimmed };

            // 1. Instance ID: #<number>
            if (trimmed[0] == '#')
            {
                if (!int.TryParse(trimmed.Substring(1), out var id))
                    return Result<ParsedPath>.Error($"Invalid instance ID in path '{trimmed}'.");
                parsed.Kind = PathKind.InstanceId;
                parsed.InstanceId = id;
                return Result<ParsedPath>.Success(parsed);
            }

            // 2. Asset DB: "Assets/..." | "Packages/..." | bare "Assets" | bare "Packages"
            if (HasNamespacePrefix(trimmed, "Assets") || HasNamespacePrefix(trimmed, "Packages"))
            {
                return ParseAsset(trimmed, parsed);
            }

            // 3. ProjectSettings: "ProjectSettings/..." | bare "ProjectSettings"
            if (HasNamespacePrefix(trimmed, "ProjectSettings"))
            {
                return ParseProjectSettings(trimmed, parsed);
            }

            // 4. Scene paths — figure out anchor.
            parsed.Kind = PathKind.Scene;

            string remainder;
            if (trimmed[0] == '/')
            {
                // Hierarchy-anchored absolute: "/World/Player".
                parsed.Anchor = PathAnchor.Hierarchy;
                remainder = trimmed.Substring(1);
                // Bare "/" is the Hierarchy root itself (the v3 anchor table:
                // "Hierarchy root — same set of root GameObjects the scene view
                // shows"). It resolves to the scene roots in PathResolver.
            }
            else
            {
                // Selection-anchored: bare, "./", or "../"+
                parsed.Anchor = PathAnchor.Selection;
                remainder = ConsumeSelectionAnchor(trimmed, parsed);
            }

            return ParseSceneTail(remainder, parsed);
        }

        // ---- helpers ----

        /// <summary>Match "Foo" exactly, "Foo/" prefix, or "Foo:" / "Foo." (component/property on namespace itself, defensive).</summary>
        private static bool HasNamespacePrefix(string s, string ns)
        {
            if (s == ns) return true;
            return s.Length > ns.Length
                && s.StartsWith(ns, StringComparison.Ordinal)
                && s[ns.Length] == '/';
        }

        /// <summary>
        /// Consumes a leading "./" or run of "../" segments and writes the
        /// resulting <see cref="ParsedPath.ParentJumps"/>. Returns the rest
        /// of the path past the anchor.
        ///
        /// Examples (anchor stripping only — segment parsing is downstream):
        ///   "."          → "" (ParentJumps=0)
        ///   "./Foo"      → "Foo" (ParentJumps=0)
        ///   ".."         → "" (ParentJumps=1)
        ///   "../Foo"     → "Foo" (ParentJumps=1)
        ///   "../../Foo"  → "Foo" (ParentJumps=2)
        ///   "Foo"        → "Foo" (ParentJumps=0)
        ///   ":Comp.prop" → ":Comp.prop" (ParentJumps=0; segments empty)
        /// </summary>
        private static string ConsumeSelectionAnchor(string input, ParsedPath parsed)
        {
            // Anchor tokens always sit at the start of the input and end
            // either at a '/' (more path follows) or at the end / a ':'
            // (component starts immediately).
            var i = 0;
            while (true)
            {
                // ".." possibly followed by '/'
                if (i + 1 < input.Length && input[i] == '.' && input[i + 1] == '.'
                    && (i + 2 == input.Length || input[i + 2] == '/' || input[i + 2] == ':'))
                {
                    parsed.ParentJumps++;
                    i += 2;
                    if (i < input.Length && input[i] == '/') i++;
                    continue;
                }
                // "." possibly followed by '/'
                if (i < input.Length && input[i] == '.'
                    && (i + 1 == input.Length || input[i + 1] == '/' || input[i + 1] == ':'))
                {
                    i += 1;
                    if (i < input.Length && input[i] == '/') i++;
                    continue;
                }
                break;
            }
            return input.Substring(i);
        }

        /// <summary>
        /// Parses the "segments[:Component][.prop...]" tail of a Scene path
        /// (or of an Asset path's InnerPath, or of a ProjectSettings path
        /// after the group). The remainder may be empty — that's a valid
        /// "the anchor itself" reference.
        /// </summary>
        private static Result<ParsedPath> ParseSceneTail(string remainder, ParsedPath parsed)
        {
            if (string.IsNullOrEmpty(remainder))
                return Result<ParsedPath>.Success(parsed);

            // Split hierarchy vs. component+property on the FIRST ':'.
            var colonIdx = remainder.IndexOf(':');
            string hierarchyPart;
            string componentPart;
            if (colonIdx >= 0)
            {
                hierarchyPart = remainder.Substring(0, colonIdx);
                componentPart = remainder.Substring(colonIdx + 1);
            }
            else
            {
                hierarchyPart = remainder;
                componentPart = null;
            }

            if (!string.IsNullOrEmpty(hierarchyPart))
            {
                // Tolerate a trailing slash ("/Foo/" ≡ "/Foo"): tab-completion
                // emits a trailing slash for containers, and it matches the
                // forgiving "cd dir/" shell convention. A leading or interior
                // empty segment ("//A", "/A//B") still errors below.
                hierarchyPart = hierarchyPart.TrimEnd('/');
                foreach (var name in hierarchyPart.Split('/'))
                {
                    if (string.IsNullOrEmpty(name))
                        return Result<ParsedPath>.Error($"Empty segment in path '{parsed.Raw}'.");
                    var segResult = ParseSegment(name);
                    if (!segResult.IsSuccess)
                        return Result<ParsedPath>.Error(segResult.ErrorMessage);
                    parsed.Segments.Add(segResult.Value);
                }
            }

            if (componentPart != null)
            {
                var dotIdx = componentPart.IndexOf('.');
                string componentSpec;
                string propertyPart = null;
                if (dotIdx >= 0)
                {
                    componentSpec = componentPart.Substring(0, dotIdx);
                    propertyPart = componentPart.Substring(dotIdx + 1);
                }
                else
                {
                    componentSpec = componentPart;
                }

                if (string.IsNullOrEmpty(componentSpec))
                    return Result<ParsedPath>.Error($"Empty component specifier in '{parsed.Raw}'.");

                var compResult = ParseComponent(componentSpec);
                if (!compResult.IsSuccess)
                    return Result<ParsedPath>.Error(compResult.ErrorMessage);
                parsed.Component = compResult.Value;

                if (!string.IsNullOrEmpty(propertyPart))
                {
                    var segResult = SplitPropertyPath(propertyPart);
                    if (!segResult.IsSuccess)
                        return Result<ParsedPath>.Error($"{segResult.ErrorMessage} in '{parsed.Raw}'.");
                    parsed.Properties = segResult.Value;
                }
            }

            return Result<ParsedPath>.Success(parsed);
        }

        /// <summary>
        /// Tokenizes the property part of a path into a flat list of segments,
        /// recognizing both dot-separated names and bracketed array indices.
        ///
        /// Examples:
        ///   "sharedMaterials[0]"      → ["sharedMaterials", "[0]"]
        ///   "arr[3].field"            → ["arr", "[3]", "field"]
        ///   "grid[2][7].name"         → ["grid", "[2]", "[7]", "name"]
        ///   "position.x"              → ["position", "x"]
        ///
        /// Index segments are stored verbatim with the brackets ("[N]") so
        /// downstream code can distinguish them from name segments cheaply.
        /// </summary>
        public static Result<List<string>> SplitPropertyPath(string propertyPart)
        {
            var result = new List<string>();
            int i = 0;
            int n = propertyPart.Length;
            bool expectName = true; // start of input or just after '.' expects a name

            while (i < n)
            {
                char c = propertyPart[i];

                if (c == '.')
                {
                    if (expectName)
                        return Result<List<string>>.Error("empty property segment");
                    expectName = true;
                    i++;
                    continue;
                }

                if (c == '[')
                {
                    int close = propertyPart.IndexOf(']', i + 1);
                    if (close < 0)
                        return Result<List<string>>.Error("unclosed '[' in property path");
                    var idxStr = propertyPart.Substring(i + 1, close - i - 1);
                    if (idxStr.Length == 0)
                        return Result<List<string>>.Error("empty array index '[]'");
                    if (!int.TryParse(idxStr, out var idx) || idx < 0)
                        return Result<List<string>>.Error($"invalid array index '[{idxStr}]'");
                    result.Add("[" + idx + "]");
                    i = close + 1;
                    expectName = false;
                    continue;
                }

                // Name: read until '.', '[', or end.
                int start = i;
                while (i < n && propertyPart[i] != '.' && propertyPart[i] != '[') i++;
                if (i == start)
                    return Result<List<string>>.Error("empty property segment");
                result.Add(propertyPart.Substring(start, i - start));
                expectName = false;
            }

            if (expectName)
                return Result<List<string>>.Error("trailing '.' in property path");
            if (result.Count == 0)
                return Result<List<string>>.Error("empty property path");
            return Result<List<string>>.Success(result);
        }

        private static Result<ParsedPath> ParseAsset(string trimmed, ParsedPath parsed)
        {
            parsed.Kind = PathKind.Asset;

            // Asset paths can contain dots in folder names
            // (`Assets/Stuff.v2/Hat.prefab`) but never a colon — so the first
            // ':' marks the start of `:Component[.prop]`. Peel that off first
            // so the asset-vs-sub-asset split below sees only filesystem-y
            // characters.
            var colonIdx = trimmed.IndexOf(':');
            string assetPart;
            string componentTail = null;
            if (colonIdx >= 0)
            {
                assetPart = trimmed.Substring(0, colonIdx);
                componentTail = trimmed.Substring(colonIdx); // includes leading ':'
            }
            else
            {
                assetPart = trimmed;
            }

            // "//" splits the on-disk asset path from the sub-asset path.
            // We use "//" instead of a single "/" because asset folders may
            // legally contain dots (e.g. Assets/Stuff.v2/Hat.prefab).
            var doubleSlash = assetPart.IndexOf("//", StringComparison.Ordinal);
            if (doubleSlash >= 0)
            {
                parsed.AssetPath = assetPart.Substring(0, doubleSlash);
                parsed.InnerPath = assetPart.Substring(doubleSlash + 2);
            }
            else
            {
                parsed.AssetPath = assetPart;
                parsed.InnerPath = "";
            }

            // Compose the tail ParseSceneTail consumes:
            // sub-asset segments + optional ':Component[.prop]'.
            var tail = parsed.InnerPath + (componentTail ?? "");
            if (string.IsNullOrEmpty(tail))
                return Result<ParsedPath>.Success(parsed);
            return ParseSceneTail(tail, parsed);
        }

        private static Result<ParsedPath> ParseProjectSettings(string trimmed, ParsedPath parsed)
        {
            parsed.Kind = PathKind.ProjectSettings;
            const string Prefix = "ProjectSettings";
            if (trimmed == Prefix)
            {
                parsed.SettingsGroup = "";
                return Result<ParsedPath>.Success(parsed);
            }
            // After "ProjectSettings/"
            var rest = trimmed.Substring(Prefix.Length + 1);
            if (string.IsNullOrEmpty(rest))
            {
                parsed.SettingsGroup = "";
                return Result<ParsedPath>.Success(parsed);
            }

            // The first segment is the settings group name; component and
            // property follow normal rules. There can be additional path
            // segments after the group (rarely needed today, but we keep
            // the door open).
            //
            // Special-case: a property reference *directly* on the group
            // (e.g. "ProjectSettings/Physics.gravity") is the common form,
            // so we treat the dot conventionally as a property separator
            // when it appears before any "/" and there's no ":" earlier.

            // Find the boundary of the group token: first '/' or ':' or '.'
            var boundary = FindGroupBoundary(rest);
            string group;
            string remainder;
            char boundaryChar;
            if (boundary < 0)
            {
                group = rest;
                remainder = "";
                boundaryChar = '\0';
            }
            else
            {
                group = rest.Substring(0, boundary);
                remainder = rest.Substring(boundary);
                boundaryChar = rest[boundary];
            }

            if (string.IsNullOrEmpty(group))
                return Result<ParsedPath>.Error(
                    $"ProjectSettings path needs a group, e.g. 'ProjectSettings/Physics' (got '{trimmed}').");
            parsed.SettingsGroup = group;

            // No remainder → just the group root.
            if (string.IsNullOrEmpty(remainder))
                return Result<ParsedPath>.Success(parsed);

            switch (boundaryChar)
            {
                case '/':
                    return ParseSceneTail(remainder.Substring(1), parsed);
                case ':':
                    // ":Component[.prop]" — pass straight through to the tail parser.
                    return ParseSceneTail(remainder, parsed);
                case '.':
                    // "ProjectSettings/Physics.gravity" — synthesize a
                    // ":__settings.<prop>" tail. We use a sentinel component
                    // name so callers can recognize the "settings group as
                    // serialized object, properties hang directly off it"
                    // shape without inventing a separate code path.
                    return ParseSceneTail(":" + SettingsRootSentinel + remainder, parsed);
                default:
                    return Result<ParsedPath>.Error($"Unexpected character at '{remainder}' in '{trimmed}'.");
            }
        }

        /// <summary>
        /// Sentinel component name used when a ProjectSettings path uses the
        /// "Group.prop" shorthand (i.e. no explicit ":Component" segment).
        /// Recognized by the resolver as "the settings group's serialized
        /// object itself".
        /// </summary>
        public const string SettingsRootSentinel = "__settings";

        private static int FindGroupBoundary(string s)
        {
            for (var i = 0; i < s.Length; i++)
            {
                var c = s[i];
                if (c == '/' || c == ':' || c == '.') return i;
            }
            return -1;
        }

        private static Result<PathSegment> ParseSegment(string text)
        {
            var bracket = text.IndexOf('[');
            if (bracket < 0)
                return Result<PathSegment>.Success(new PathSegment { Name = text });
            if (!text.EndsWith("]", StringComparison.Ordinal))
                return Result<PathSegment>.Error($"Unclosed '[' in segment '{text}'.");
            var name = text.Substring(0, bracket);
            if (string.IsNullOrEmpty(name))
                return Result<PathSegment>.Error($"Empty name before '[' in segment '{text}'.");
            var idxText = text.Substring(bracket + 1, text.Length - bracket - 2);
            if (!int.TryParse(idxText, out var idx) || idx < 0)
                return Result<PathSegment>.Error($"Invalid index '[{idxText}]' in segment '{text}'.");
            return Result<PathSegment>.Success(new PathSegment { Name = name, Index = idx });
        }

        /// <summary>
        /// Public entry point for the <c>Type[n]</c> mini-grammar — used by
        /// tools that take a bare component spec (e.g. <c>component remove</c>).
        /// </summary>
        public static Result<ComponentRef> ParseComponentSpec(string text) => ParseComponent(text);

        private static Result<ComponentRef> ParseComponent(string text)
        {
            var bracket = text.IndexOf('[');
            if (bracket < 0)
                return Result<ComponentRef>.Success(new ComponentRef { TypeName = text });
            if (!text.EndsWith("]", StringComparison.Ordinal))
                return Result<ComponentRef>.Error($"Unclosed '[' in component '{text}'.");
            var typeName = text.Substring(0, bracket);
            if (string.IsNullOrEmpty(typeName))
                return Result<ComponentRef>.Error($"Empty type before '[' in component '{text}'.");
            var idxText = text.Substring(bracket + 1, text.Length - bracket - 2);
            if (!int.TryParse(idxText, out var idx) || idx < 0)
                return Result<ComponentRef>.Error($"Invalid index '[{idxText}]' in component '{text}'.");
            return Result<ComponentRef>.Success(new ComponentRef { TypeName = typeName, Index = idx });
        }
    }
}
