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



using Newtonsoft.Json.Linq;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace UnityCliConnector.Tools
{
	/// <summary>
	/// Create a new GameObject (empty or primitive), or instantiate a prefab.
	/// Returns the canonical path of the new object, enabling pipes into
	/// <c>set</c>, <c>component add</c>, etc.
	///
	/// All creations register with Undo so the Editor's undo stack records them.
	/// </summary>
	[UnityCliTool(Name = "create",
		Description = "Create a new GameObject, primitive, or prefab instance. Returns canonical path.")]
	public static class Create
	{
		public class Parameters
		{
			[ToolParameter("Type: empty, or a primitive (cube, sphere, capsule, cylinder, plane, quad).", Required = true)]
			public string Type { get; set; }

			[ToolParameter("Parent path and name: parentpath/name. Parent must exist.", Required = true)]
			public string Path { get; set; }

			[ToolParameter("Create a prefab instance from this asset instead of an empty or primitive.")]
			public string Prefab { get; set; }
		}

		public static object HandleCommand(JObject @params)
		{
			var p = new ToolParams(@params);

			// Positional layout: [type, path] or via flags.
			var args = p.GetRaw("args") as JArray;
			var typeArg = p.Get("type")
				?? (args != null && args.Count > 0 ? args[0]?.ToString() : null);
			var pathArg = p.Get("path")
				?? (args != null && args.Count > 1 ? args[1]?.ToString() : null);
			var prefabArg = p.Get("prefab");

			if (string.IsNullOrWhiteSpace(pathArg))
				return new ErrorResponse("create requires a path (parentpath/name, or /name for scene root).");

			// Parse path into parent and name.
			//   "World/Enemies/Foo"  → parent="World/Enemies", name="Foo"
			//   "/Foo"               → parent="", name="Foo"   (scene root)
			//   "/"                  → empty name, reject
			//   "Foo"                → reject (need an explicit parent or leading slash)
			var lastSlash = pathArg.LastIndexOf('/');
			if (lastSlash < 0)
				return new ErrorResponse(
					"Path must include a parent. Use 'parent/name', or '/name' to create at the scene root.");

			var parentPath = pathArg.Substring(0, lastSlash);
			var name = pathArg.Substring(lastSlash + 1);
			if (string.IsNullOrEmpty(name))
				return new ErrorResponse("Name segment is empty. Use 'parent/name' or '/name'.");

			// Empty parentPath = scene root (single leading slash). Null `parent`
			// signals "no GameObject parent — root the new object directly under
			// the active scene." Matches cp/mv's scene-root semantics.
			GameObject parent = null;
			if (!string.IsNullOrEmpty(parentPath))
			{
				var parseResult = PathParser.Parse(parentPath);
				if (!parseResult.IsSuccess) return ErrorResponse.FromResult(parseResult);
				var parentRes = PathResolver.ResolveGameObject(parseResult.Value);
				if (!parentRes.IsSuccess) return ErrorResponse.FromResult(parentRes);
				parent = parentRes.Value;
			}

			GameObject created = null;
			var parentTransform = parent != null ? parent.transform : null;

			if (!string.IsNullOrEmpty(prefabArg))
			{
				// Instantiate prefab.
				var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabArg);
				if (prefab == null)
					return new ErrorResponse($"Prefab not found at '{prefabArg}'.");

				created = PrefabUtility.InstantiatePrefab(prefab, parentTransform) as GameObject;
				if (created == null)
					return new ErrorResponse($"Failed to instantiate prefab '{prefabArg}'.");
				created.name = name;
				Undo.RegisterCreatedObjectUndo(created, $"Create prefab instance {name}");
			}
			else if (string.IsNullOrWhiteSpace(typeArg) || typeArg.ToLower() == "empty")
			{
				// Create empty GameObject.
				created = new GameObject(name);
				if (parentTransform != null) created.transform.SetParent(parentTransform);
				Undo.RegisterCreatedObjectUndo(created, $"Create Empty {name}");
			}
			else
			{
				// Create primitive.
				var primType = ParsePrimitiveType(typeArg);
				if (primType == null)
					return new ErrorResponse(
						$"Unknown type '{typeArg}'. Use: empty, cube, sphere, capsule, cylinder, plane, quad.");

				created = GameObject.CreatePrimitive(primType.Value);
				if (created == null)
					return new ErrorResponse($"Failed to create primitive '{typeArg}'.");

				created.name = name;
				if (parentTransform != null) created.transform.SetParent(parentTransform);
				Undo.RegisterCreatedObjectUndo(created, $"Create primitive {name}");
			}

			if (parent != null) EditorUtility.SetDirty(parent);
			EditorUtility.SetDirty(created);

			var canonicalPath = PathResolver.GetCanonicalPath(created);
			var format = (p.Get("format") ?? "plain").ToLowerInvariant();

			// Pipe-friendly default: response data is the canonical path so
			// `create … | set …`, `create … | component add … <type>`, etc.
			// work as the help text promises. `--json` opts into the full
			// record for tooling.
			if (format == "json")
			{
				return new SuccessResponse(canonicalPath, new Dictionary<string, object>
				{
					["path"] = canonicalPath,
					["name"] = name,
					["parent"] = parent != null ? PathResolver.GetCanonicalPath(parent) : "/",
					["type"] = typeArg ?? "prefab",
					["instanceId"] = created.GetInstanceID(),
				});
			}
			return new SuccessResponse(canonicalPath, canonicalPath);
		}

		private static PrimitiveType? ParsePrimitiveType(string name)
		{
			return name.ToLower() switch
			{
				"cube" => PrimitiveType.Cube,
				"sphere" => PrimitiveType.Sphere,
				"capsule" => PrimitiveType.Capsule,
				"cylinder" => PrimitiveType.Cylinder,
				"plane" => PrimitiveType.Plane,
				"quad" => PrimitiveType.Quad,
				_ => null,
			};
		}
	}
}
