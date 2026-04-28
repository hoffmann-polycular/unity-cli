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
    /// Identifies what a parsed path points at.
    /// - Scene:      regular GameObject hierarchy path (e.g. "World/Player")
    /// - InstanceId: Unity instance ID form (e.g. "#14352")
    /// - Asset:      project asset path (e.g. "Assets/Foo.prefab"), optionally
    ///               with an inner GameObject path after "//".
    /// </summary>
    public enum PathKind
    {
        Scene,
        InstanceId,
        Asset,
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
    /// Structured representation of a parsed path.
    /// See unity-cli-reference.md §"Path Grammar" for the full grammar.
    /// </summary>
    public class ParsedPath
    {
        public PathKind Kind;

        // Kind == InstanceId
        public int InstanceId;

        // Kind == Asset
        public string AssetPath;
        public string InnerPath; // portion after "//", empty if none

        // Kind == Scene (and Kind == Asset when InnerPath is non-empty)
        public List<PathSegment> Segments = new();
        public ComponentRef Component;
        public List<string> Properties = new();

        // Original input, useful for error messages.
        public string Raw;
    }

    /// <summary>
    /// Parses user-supplied path strings into a <see cref="ParsedPath"/>.
    /// This is purely syntactic — it does NOT touch Unity state. Resolution
    /// (finding actual GameObjects / Components) is <see cref="PathResolver"/>.
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

            // Instance ID form: #<number>
            if (trimmed[0] == '#')
            {
                if (!int.TryParse(trimmed.Substring(1), out var id))
                    return Result<ParsedPath>.Error($"Invalid instance ID in path '{trimmed}'.");
                parsed.Kind = PathKind.InstanceId;
                parsed.InstanceId = id;
                return Result<ParsedPath>.Success(parsed);
            }

            // Asset form: "Assets/..." or exactly "Assets"
            string workingPath;
            if (trimmed == "Assets" || trimmed.StartsWith("Assets/", StringComparison.Ordinal))
            {
                parsed.Kind = PathKind.Asset;
                var doubleSlash = trimmed.IndexOf("//", StringComparison.Ordinal);
                if (doubleSlash >= 0)
                {
                    parsed.AssetPath = trimmed.Substring(0, doubleSlash);
                    parsed.InnerPath = trimmed.Substring(doubleSlash + 2);
                    workingPath = parsed.InnerPath;
                }
                else
                {
                    parsed.AssetPath = trimmed;
                    parsed.InnerPath = "";
                    return Result<ParsedPath>.Success(parsed);
                }
                if (string.IsNullOrEmpty(workingPath))
                    return Result<ParsedPath>.Success(parsed);
            }
            else
            {
                parsed.Kind = PathKind.Scene;
                workingPath = trimmed;
            }

            // Split hierarchy vs. component+property on the FIRST ':'.
            var colonIdx = workingPath.IndexOf(':');
            string hierarchyPart;
            string componentPart;
            if (colonIdx >= 0)
            {
                hierarchyPart = workingPath.Substring(0, colonIdx);
                componentPart = workingPath.Substring(colonIdx + 1);
            }
            else
            {
                hierarchyPart = workingPath;
                componentPart = null;
            }

            if (!string.IsNullOrEmpty(hierarchyPart))
            {
                foreach (var name in hierarchyPart.Split('/'))
                {
                    if (string.IsNullOrEmpty(name))
                        return Result<ParsedPath>.Error($"Empty segment in path '{trimmed}'.");
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
                    return Result<ParsedPath>.Error($"Empty component specifier in '{trimmed}'.");

                var compResult = ParseComponent(componentSpec);
                if (!compResult.IsSuccess)
                    return Result<ParsedPath>.Error(compResult.ErrorMessage);
                parsed.Component = compResult.Value;

                if (!string.IsNullOrEmpty(propertyPart))
                {
                    foreach (var prop in propertyPart.Split('.'))
                    {
                        if (string.IsNullOrEmpty(prop))
                            return Result<ParsedPath>.Error($"Empty property segment in '{trimmed}'.");
                        parsed.Properties.Add(prop);
                    }
                }
            }

            return Result<ParsedPath>.Success(parsed);
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
