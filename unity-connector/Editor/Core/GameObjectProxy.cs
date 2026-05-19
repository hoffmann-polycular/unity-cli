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
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace UnityCliConnector
{
	/// <summary>
	/// Virtual ":GameObject" pseudo-component that exposes core GameObject
	/// fields — name, tag, layer, activeSelf, isStatic, … — through the
	/// same get / set / inspect path grammar as real components.
	///
	/// Examples:
	///   unity-cli get    Player:GameObject.activeSelf
	///   unity-cli set    Player:GameObject.activeSelf false
	///   unity-cli set    Player:GameObject.layer "UI"
	///   unity-cli set    Player:GameObject.name "Hero"
	///   unity-cli inspect Player:GameObject
	/// </summary>
	internal static class GameObjectProxy
	{
		/// <summary>The pseudo-component type name used in path grammar.</summary>
		public const string PseudoTypeName = "GameObject";

		/// <summary>
		/// True when <paramref name="typeName"/> refers to this pseudo-component
		/// (case-insensitive so "gameobject", "GameObject", "GAMEOBJECT" all match).
		/// </summary>
		public static bool Is(string typeName)
			=> string.Equals(typeName, PseudoTypeName, System.StringComparison.OrdinalIgnoreCase);

		// ── readable properties ─────────────────────────────────────────────

		/// <summary>
		/// Reads a named property from <paramref name="go"/>.
		/// Property names are normalised (lowercase, dashes and underscores
		/// stripped) before matching so "active_self", "activeSelf" and
		/// "active-self" are all accepted.
		/// </summary>
		public static Result<object> Get(GameObject go, string prop)
		{
			switch (Normalize(prop))
			{
				case "name":              return Ok(go.name);
				case "tag":               return Ok(go.tag);
				case "layer":             return Ok(go.layer);
				case "layername":         return Ok(LayerMask.LayerToName(go.layer));
				case "activeself":
				case "active":            return Ok(go.activeSelf);
				case "activeinhierarchy": return Ok(go.activeInHierarchy);
				case "isstatic":          return Ok(go.isStatic);
				case "instanceid":        return Ok((object)go.GetInstanceID());
				default:
					return Result<object>.Error(
						$":GameObject has no property '{prop}'. " +
						"Readable: name, tag, layer, layerName, activeSelf, activeInHierarchy, isStatic, instanceId.",
						ErrorKind.NotFound);
			}
		}

		// ── writable properties ─────────────────────────────────────────────

		/// <summary>
		/// Writes a named property on <paramref name="go"/>, recording an Undo
		/// entry. Returns a set-result dictionary on success.
		/// </summary>
		public static Result<Dictionary<string, object>> Set(
			GameObject go, string prop, JToken rawValue)
		{
			Undo.RecordObject(go, $"Set {go.name}.{prop}");

			switch (Normalize(prop))
			{
				case "name":
				{
					var old = go.name;
					var requested = rawValue?.ToString() ?? "";
					// Skip the assignment when nothing would change — avoids
					// dirtying the GameObject (and registering a prefab
					// override) for a no-op.
					if (old == requested) return OkSet(go, "name", old, old);
					go.name = requested;
					EditorUtility.SetDirty(go);
					return OkSet(go, "name", old, go.name);
				}

				case "tag":
				{
					var old = go.tag;
					var tag = rawValue?.ToString() ?? "Untagged";
					if (old == tag) return OkSet(go, "tag", old, old);
					try { go.tag = tag; }
					catch (System.Exception ex)
					{
						return Result<Dictionary<string, object>>.Error(
							$"Invalid tag '{tag}': {ex.Message}");
					}
					EditorUtility.SetDirty(go);
					return OkSet(go, "tag", old, go.tag);
				}

				case "layer":
				{
					var old = go.layer;
					int newLayer;
					if (rawValue != null && rawValue.Type == JTokenType.Integer)
					{
						newLayer = rawValue.Value<int>();
					}
					else
					{
						var s = rawValue?.ToString() ?? "";
						if (int.TryParse(s, out var parsed))
						{
							newLayer = parsed;
						}
						else
						{
							newLayer = LayerMask.NameToLayer(s);
							if (newLayer < 0)
								return Result<Dictionary<string, object>>.Error(
									$"Unknown layer '{s}'. Use a layer name or an integer 0–31.");
						}
					}
					if (old == newLayer) return OkSet(go, "layer", old, old);
					go.layer = newLayer;
					EditorUtility.SetDirty(go);
					return OkSet(go, "layer", old, go.layer);
				}

				case "activeself":
				case "active":
				{
					var old = go.activeSelf;
					var newActive = ParseBool(rawValue);
					if (old == newActive) return OkSet(go, "activeSelf", old, old);
					go.SetActive(newActive);
					EditorUtility.SetDirty(go);
					return OkSet(go, "activeSelf", old, go.activeSelf);
				}

				case "isstatic":
				{
					var old = go.isStatic;
					var newStatic = ParseBool(rawValue);
					if (old == newStatic) return OkSet(go, "isStatic", old, old);
					go.isStatic = newStatic;
					EditorUtility.SetDirty(go);
					return OkSet(go, "isStatic", old, go.isStatic);
				}

				case "activeinhierarchy":
				case "instanceid":
					return Result<Dictionary<string, object>>.Error(
						$"Property '{prop}' is read-only.", ErrorKind.Usage);

				default:
					return Result<Dictionary<string, object>>.Error(
						$":GameObject has no writable property '{prop}'. " +
						"Writable: name, tag, layer, activeSelf (alias: active), isStatic.",
						ErrorKind.NotFound);
			}
		}

		// ── inspect ─────────────────────────────────────────────────────────

		/// <summary>Snapshot of all readable fields in a stable key order.</summary>
		public static Dictionary<string, object> InspectAll(GameObject go)
		{
			return new Dictionary<string, object>
			{
				["name"]              = go.name,
				["activeSelf"]        = go.activeSelf,
				["activeInHierarchy"] = go.activeInHierarchy,
				["tag"]               = go.tag,
				["layer"]             = go.layer,
				["layerName"]         = LayerMask.LayerToName(go.layer),
				["isStatic"]          = go.isStatic,
				["instanceId"]        = go.GetInstanceID(),
			};
		}

		// ── helpers ──────────────────────────────────────────────────────────

		private static string Normalize(string s)
			=> (s ?? "").ToLowerInvariant().Replace("-", "").Replace("_", "");

		private static bool ParseBool(JToken tok)
		{
			if (tok == null) return false;
			if (tok.Type == JTokenType.Boolean) return tok.Value<bool>();
			var s = tok.ToString().ToLowerInvariant();
			return s == "true" || s == "1" || s == "yes" || s == "on";
		}

		private static Result<object> Ok(object v) => Result<object>.Success(v);

		private static Result<Dictionary<string, object>> OkSet(
			GameObject go, string prop, object oldValue, object newValue)
		{
			return Result<Dictionary<string, object>>.Success(
				new Dictionary<string, object>
				{
					["path"]      = PathResolver.GetCanonicalPath(go),
					["component"] = PseudoTypeName,
					["property"]  = prop,
					["type"]      = "GameObjectProperty",
					["oldValue"]  = oldValue,
					["newValue"]  = newValue,
				});
		}
	}
}
