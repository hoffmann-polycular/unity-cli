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
using UnityEditor;
using UnityEngine;

namespace UnityCliConnector
{
	/// <summary>
	/// Walks a <see cref="SerializedProperty"/> and produces plain
	/// JSON-serializable values (primitives, arrays, dictionaries).
	///
	/// Matches what the Inspector shows, not the raw C# field layout:
	/// Unity-internal meta fields (<c>m_ObjectHideFlags</c>,
	/// <c>m_CorrespondingSourceObject</c>, etc.) are dropped from
	/// generic-object dumps; keys are de-prefixed (<c>m_LocalPosition</c>
	/// → <c>localPosition</c>) so output reads like the CLI grammar.
	/// </summary>
	public static class SerializedPropertyReader
	{
		// Meta properties Unity inserts into every serialized object. Noise
		// for users, so we skip them in whole-object dumps.
		private static readonly HashSet<string> SkipGeneric = new HashSet<string>
		{
			"m_ObjectHideFlags",
			"m_CorrespondingSourceObject",
			"m_PrefabInstance",
			"m_PrefabAsset",
			"m_GameObject",
			"m_EditorHideFlags",
			"m_EditorClassIdentifier",
			"m_Script",
		};

		/// <summary>
		/// Reads a single property into a JSON-safe value.
		/// </summary>
		public static object Read(SerializedProperty prop)
		{
			if (prop == null) return null;
			switch (prop.propertyType)
			{
				case SerializedPropertyType.Integer:
				{
					// Many serialized fields are typed as C# enums but Unity
					// stores them as Integer at the SO layer (e.g.
					// ModelImporter.materialImportMode →
					// ModelImporterMaterialImportMode). The standard Enum
					// case below only fires for fields Unity tags as enum at
					// the SO level; this reflection probe catches the rest,
					// so `inspect` shows "InPrefab" instead of "0".
					var enumName = TryResolveEnumName(prop);
					if (enumName != null) return enumName;
					return prop.intValue;
				}
				case SerializedPropertyType.Boolean: return prop.boolValue;
				case SerializedPropertyType.Float: return prop.floatValue;
				case SerializedPropertyType.String: return prop.stringValue;
				case SerializedPropertyType.Color:
				{
					var c = prop.colorValue;
					return new Dictionary<string, object>
					{
						["r"] = c.r,
						["g"] = c.g,
						["b"] = c.b,
						["a"] = c.a,
					};
				}
				case SerializedPropertyType.ObjectReference:
					return ReadObjectReference(prop.objectReferenceValue);
				case SerializedPropertyType.LayerMask: return prop.intValue;
				case SerializedPropertyType.Enum:
				{
					var idx = prop.enumValueIndex;
					var names = prop.enumNames;
					if (idx >= 0 && names != null && idx < names.Length)
						return names[idx];
					return prop.intValue;
				}
				case SerializedPropertyType.Vector2:
				{
					var v = prop.vector2Value;
					return new Dictionary<string, object> { ["x"] = v.x, ["y"] = v.y };
				}
				case SerializedPropertyType.Vector3:
				{
					var v = prop.vector3Value;
					return new Dictionary<string, object> { ["x"] = v.x, ["y"] = v.y, ["z"] = v.z };
				}
				case SerializedPropertyType.Vector4:
				{
					var v = prop.vector4Value;
					return new Dictionary<string, object> { ["x"] = v.x, ["y"] = v.y, ["z"] = v.z, ["w"] = v.w };
				}
				case SerializedPropertyType.Rect:
				{
					var r = prop.rectValue;
					return new Dictionary<string, object>
					{
						["x"] = r.x,
						["y"] = r.y,
						["width"] = r.width,
						["height"] = r.height,
					};
				}
				case SerializedPropertyType.ArraySize: return prop.intValue;
				case SerializedPropertyType.Character: return ((char)prop.intValue).ToString();
				case SerializedPropertyType.AnimationCurve: return "<AnimationCurve>";
				case SerializedPropertyType.Bounds:
				{
					var b = prop.boundsValue;
					return new Dictionary<string, object>
					{
						["center"] = new Dictionary<string, object> { ["x"] = b.center.x, ["y"] = b.center.y, ["z"] = b.center.z },
						["extents"] = new Dictionary<string, object> { ["x"] = b.extents.x, ["y"] = b.extents.y, ["z"] = b.extents.z },
					};
				}
				case SerializedPropertyType.Gradient: return "<Gradient>";
				case SerializedPropertyType.Quaternion:
				{
					var q = prop.quaternionValue;
					return new Dictionary<string, object>
					{
						["x"] = q.x,
						["y"] = q.y,
						["z"] = q.z,
						["w"] = q.w,
					};
				}
				case SerializedPropertyType.ExposedReference:
					return ReadObjectReference(prop.exposedReferenceValue);
				case SerializedPropertyType.Vector2Int:
				{
					var v = prop.vector2IntValue;
					return new Dictionary<string, object> { ["x"] = v.x, ["y"] = v.y };
				}
				case SerializedPropertyType.Vector3Int:
				{
					var v = prop.vector3IntValue;
					return new Dictionary<string, object> { ["x"] = v.x, ["y"] = v.y, ["z"] = v.z };
				}
				case SerializedPropertyType.RectInt:
				{
					var r = prop.rectIntValue;
					return new Dictionary<string, object>
					{
						["x"] = r.x,
						["y"] = r.y,
						["width"] = r.width,
						["height"] = r.height,
					};
				}
				case SerializedPropertyType.BoundsInt:
				{
					var b = prop.boundsIntValue;
					return new Dictionary<string, object>
					{
						["position"] = new Dictionary<string, object> { ["x"] = b.position.x, ["y"] = b.position.y, ["z"] = b.position.z },
						["size"] = new Dictionary<string, object> { ["x"] = b.size.x, ["y"] = b.size.y, ["z"] = b.size.z },
					};
				}
				case SerializedPropertyType.Generic:
				case SerializedPropertyType.ManagedReference:
					return ReadComposite(prop);
				default:
					return $"<{prop.propertyType}>";
			}
		}

		/// <summary>
		/// Reads every visible top-level property of a SerializedObject into
		/// a <c>name → value</c> dictionary. Meta fields are skipped.
		/// </summary>
		public static Dictionary<string, object> ReadAll(SerializedObject so, bool overridesOnly = false)
		{
			var result = new Dictionary<string, object>();
			if (so == null) return result;

			var it = so.GetIterator();
			var enterChildren = true;
			while (it.NextVisible(enterChildren))
			{
				enterChildren = false;
				if (SkipGeneric.Contains(it.name)) continue;
				if (overridesOnly && !it.prefabOverride) continue;
				result[PathResolver.NormalizeSerializedName(it.name)] = Read(it);
			}
			return result;
		}

		// ---- internals ----

		private static object ReadComposite(SerializedProperty prop)
		{
			if (prop.isArray && prop.propertyType != SerializedPropertyType.String)
			{
				var list = new List<object>(prop.arraySize);
				for (var i = 0; i < prop.arraySize; i++)
					list.Add(Read(prop.GetArrayElementAtIndex(i)));
				return list;
			}

			var dict = new Dictionary<string, object>();
			var it = prop.Copy();
			var end = prop.GetEndProperty();
			var enterChildren = true;
			while (it.NextVisible(enterChildren) && !SerializedProperty.EqualContents(it, end))
			{
				enterChildren = false;
				dict[PathResolver.NormalizeSerializedName(it.name)] = Read(it);
			}
			return dict;
		}

		private static object ReadObjectReference(Object o)
		{
			if (o == null) return null;

			if (o is GameObject go)
			{
				return new Dictionary<string, object>
				{
					["type"] = "GameObject",
					["path"] = PathResolver.GetCanonicalPath(go),
					["instanceId"] = go.GetInstanceID(),
				};
			}

			if (o is Component c)
			{
				return new Dictionary<string, object>
				{
					["type"] = c.GetType().Name,
					["path"] = PathResolver.GetCanonicalPath(c.gameObject),
					["instanceId"] = c.GetInstanceID(),
				};
			}

			var assetPath = AssetDatabase.GetAssetPath(o);
			if (!string.IsNullOrEmpty(assetPath))
			{
				return new Dictionary<string, object>
				{
					["type"] = o.GetType().Name,
					["asset"] = assetPath,
					["name"] = o.name,
					["instanceId"] = o.GetInstanceID(),
				};
			}

			return new Dictionary<string, object>
			{
				["type"] = o.GetType().Name,
				["name"] = o.name,
				["instanceId"] = o.GetInstanceID(),
			};
		}

		// Returns the human-readable enum-value name when an SO-Integer
		// property is backed by a C# enum field; null otherwise. Walks the
		// target type via reflection along prop.propertyPath, matching
		// `m_Name` ↔ `name` casing tricks Unity's serializer uses.
		private static string TryResolveEnumName(SerializedProperty prop)
		{
			try
			{
				var target = prop.serializedObject?.targetObject;
				if (target == null) return null;
				var fieldType = ResolveFieldType(target.GetType(), prop.propertyPath);
				if (fieldType == null || !fieldType.IsEnum) return null;

				// Long-keyed Flags enums need flags-formatting; the standard
				// `Enum.GetName` returns null for combinations. Format with
				// Enum.ToObject covers both single and flags cases.
				var enumVal = System.Enum.ToObject(fieldType, prop.intValue);
				return enumVal?.ToString();
			}
			catch
			{
				return null;
			}
		}

		// Walks a Unity-serialised property path against a C# type to find
		// the field's declared type. Handles plain field names, `m_` prefix
		// folding, and array-element syntax (`Array.data[idx]`).
		//
		// Falls back to C# property lookup when a field isn't found — many
		// Unity types (especially AssetImporter subclasses) expose
		// serialized settings only as typed C# properties; the actual data
		// lives in native code with no reflectable backing field. The
		// SerializedObject layer reports those as Integer; reading the
		// property's return type tells us they're really enums.
		private static System.Type ResolveFieldType(System.Type type, string propertyPath)
		{
			if (type == null || string.IsNullOrEmpty(propertyPath)) return null;
			var bf = System.Reflection.BindingFlags.Instance
				| System.Reflection.BindingFlags.Public
				| System.Reflection.BindingFlags.NonPublic;

			var current = type;
			var segments = propertyPath.Split('.');
			for (var i = 0; i < segments.Length; i++)
			{
				var seg = segments[i];
				if (current == null) return null;

				// `Array.data[idx]` — when stepping into an array element,
				// pull the element type and skip the `data[idx]` segment.
				if (seg == "Array" && i + 1 < segments.Length
					&& segments[i + 1].StartsWith("data[", System.StringComparison.Ordinal))
				{
					if (current.IsArray) current = current.GetElementType();
					else if (current.IsGenericType) current = current.GetGenericArguments()[0];
					else return null;
					i++; // consume `data[idx]`
					continue;
				}

				var memberType = FindMemberType(current, seg, bf);
				if (memberType == null) return null;
				current = memberType;
			}
			return current;
		}

		private static System.Type FindMemberType(
			System.Type t, string name, System.Reflection.BindingFlags bf)
		{
			// Try field first (most common — direct SO field name).
			// Then `m_` ↔ camelCase folding.
			// Then C# property (covers native-backed importer settings:
			// ModelImporter.materialImportMode etc. have no field but the
			// public property is typed as the enum).
			for (var cur = t; cur != null && cur != typeof(object); cur = cur.BaseType)
			{
				var f = cur.GetField(name, bf);
				if (f != null) return f.FieldType;

				string alt = null;
				if (name.StartsWith("m_", System.StringComparison.Ordinal) && name.Length > 2)
				{
					// `m_MaterialImportMode` → `materialImportMode`
					alt = char.ToLowerInvariant(name[2]) + name.Substring(3);
				}
				else if (name.Length > 0)
				{
					// `materialImportMode` → `m_MaterialImportMode`
					alt = "m_" + char.ToUpperInvariant(name[0]) + name.Substring(1);
				}
				if (alt != null)
				{
					f = cur.GetField(alt, bf);
					if (f != null) return f.FieldType;
				}

				// Property lookup: try the original name and the de-prefixed
				// camelCase form. Properties are public in Unity's API
				// surface, so the public binding flag suffices.
				var pi = cur.GetProperty(name, bf);
				if (pi != null && pi.GetIndexParameters().Length == 0) return pi.PropertyType;
				if (alt != null && !alt.StartsWith("m_", System.StringComparison.Ordinal))
				{
					pi = cur.GetProperty(alt, bf);
					if (pi != null && pi.GetIndexParameters().Length == 0) return pi.PropertyType;
				}
			}
			return null;
		}
	}
}
