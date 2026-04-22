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
using System.Globalization;
using UnityEditor;
using UnityEngine;

namespace UnityCliConnector
{
	/// <summary>
	/// Coerces a CLI/JSON value into the C# type a <see cref="SerializedProperty"/>
	/// expects, then writes it.
	///
	/// Accepts both JSON-shaped input (objects/arrays/numbers/bools) and the
	/// flat string form most CLI users will type:
	///   - vectors: <c>"1,2,3"</c> or <c>{"x":1,"y":2,"z":3}</c>
	///   - colors:  <c>"#RRGGBB[AA]"</c>, <c>"r,g,b[,a]"</c>, or <c>{"r":..,"g":..,"b":..,"a":..}</c>
	///   - quat:    3 components → Euler degrees, 4 components → raw <c>(x,y,z,w)</c>
	///   - object:  <c>"#&lt;id&gt;"</c>, <c>"Assets/..."</c>, scene path, or <c>null</c>
	///
	/// Generic structs and managed references are rejected — write to a leaf
	/// child instead (<c>.position.x</c>) so the user sees obvious failure
	/// modes rather than silently-clobbered nested data.
	/// </summary>
	public static class SerializedPropertyWriter
	{
		public static Result<bool> Write(SerializedProperty prop, JToken value)
		{
			if (prop == null) return Result<bool>.Error("Property is null.");

			switch (prop.propertyType)
			{
				case SerializedPropertyType.Integer:
				case SerializedPropertyType.LayerMask:
				case SerializedPropertyType.ArraySize:
					if (!TryAsLong(value, out var i)) return TypeError(prop, "integer");
					prop.intValue = (int)i;
					return Ok();

				case SerializedPropertyType.Boolean:
					if (!TryAsBool(value, out var b)) return TypeError(prop, "boolean");
					prop.boolValue = b;
					return Ok();

				case SerializedPropertyType.Float:
					if (!TryAsFloat(value, out var f)) return TypeError(prop, "float");
					prop.floatValue = f;
					return Ok();

				case SerializedPropertyType.String:
					prop.stringValue = AsString(value) ?? "";
					return Ok();

				case SerializedPropertyType.Character:
				{
					var s = AsString(value);
					if (string.IsNullOrEmpty(s)) return TypeError(prop, "single character");
					prop.intValue = s[0];
					return Ok();
				}

				case SerializedPropertyType.Color:
					if (!TryAsColor(value, out var col)) return TypeError(prop, "color");
					prop.colorValue = col;
					return Ok();

				case SerializedPropertyType.Vector2:
					if (!TryAsFloats(value, 2, out var v2)) return TypeError(prop, "Vector2");
					prop.vector2Value = new Vector2(v2[0], v2[1]);
					return Ok();

				case SerializedPropertyType.Vector3:
					if (!TryAsFloats(value, 3, out var v3)) return TypeError(prop, "Vector3");
					prop.vector3Value = new Vector3(v3[0], v3[1], v3[2]);
					return Ok();

				case SerializedPropertyType.Vector4:
					if (!TryAsFloats(value, 4, out var v4)) return TypeError(prop, "Vector4");
					prop.vector4Value = new Vector4(v4[0], v4[1], v4[2], v4[3]);
					return Ok();

				case SerializedPropertyType.Vector2Int:
					if (!TryAsInts(value, 2, out var v2i)) return TypeError(prop, "Vector2Int");
					prop.vector2IntValue = new Vector2Int(v2i[0], v2i[1]);
					return Ok();

				case SerializedPropertyType.Vector3Int:
					if (!TryAsInts(value, 3, out var v3i)) return TypeError(prop, "Vector3Int");
					prop.vector3IntValue = new Vector3Int(v3i[0], v3i[1], v3i[2]);
					return Ok();

				case SerializedPropertyType.Quaternion:
					return WriteQuaternion(prop, value);

				case SerializedPropertyType.Rect:
					if (!TryAsFloats(value, 4, out var r4)) return TypeError(prop, "Rect (x,y,width,height)");
					prop.rectValue = new Rect(r4[0], r4[1], r4[2], r4[3]);
					return Ok();

				case SerializedPropertyType.RectInt:
					if (!TryAsInts(value, 4, out var ri4)) return TypeError(prop, "RectInt (x,y,width,height)");
					prop.rectIntValue = new RectInt(ri4[0], ri4[1], ri4[2], ri4[3]);
					return Ok();

				case SerializedPropertyType.Bounds:
					return WriteBounds(prop, value);

				case SerializedPropertyType.BoundsInt:
					return WriteBoundsInt(prop, value);

				case SerializedPropertyType.Enum:
					return WriteEnum(prop, value);

				case SerializedPropertyType.ObjectReference:
					return WriteObjectReference(prop, value);

				case SerializedPropertyType.ExposedReference:
				{
					var objRes = ResolveObjectFromValue(value, expected: null);
					if (!objRes.IsSuccess) return Result<bool>.Error(objRes.ErrorMessage);
					prop.exposedReferenceValue = objRes.Value;
					return Ok();
				}

				case SerializedPropertyType.AnimationCurve:
				case SerializedPropertyType.Gradient:
					return Result<bool>.Error(
						$"Setting {prop.propertyType} values is not supported yet.");

				case SerializedPropertyType.Generic:
				case SerializedPropertyType.ManagedReference:
					return Result<bool>.Error(
						$"Cannot set composite property '{prop.name}' directly. Set a leaf field instead (e.g. '.field.x').");

				default:
					return Result<bool>.Error($"Unsupported property type: {prop.propertyType}.");
			}
		}

		// ---- composite writers ----

		private static Result<bool> WriteQuaternion(SerializedProperty prop, JToken value)
		{
			// 4 components → raw quaternion; 3 components → Euler degrees.
			if (value is JObject obj && obj["eulerAngles"] != null)
			{
				if (!TryAsFloats(obj["eulerAngles"], 3, out var eu)) return TypeError(prop, "Quaternion eulerAngles");
				prop.quaternionValue = Quaternion.Euler(eu[0], eu[1], eu[2]);
				return Ok();
			}
			if (TryAsFloats(value, 4, out var q4))
			{
				prop.quaternionValue = new Quaternion(q4[0], q4[1], q4[2], q4[3]);
				return Ok();
			}
			if (TryAsFloats(value, 3, out var e3))
			{
				prop.quaternionValue = Quaternion.Euler(e3[0], e3[1], e3[2]);
				return Ok();
			}
			return TypeError(prop, "Quaternion (3 Euler degrees or 4 raw components)");
		}

		private static Result<bool> WriteBounds(SerializedProperty prop, JToken value)
		{
			if (value is JObject obj
				&& TryAsFloats(obj["center"], 3, out var c)
				&& (TryAsFloats(obj["extents"], 3, out var e) || TryAsFloats(obj["size"], 3, out e)))
			{
				var center = new Vector3(c[0], c[1], c[2]);
				var size = obj["size"] != null ? new Vector3(e[0], e[1], e[2]) : new Vector3(e[0] * 2, e[1] * 2, e[2] * 2);
				prop.boundsValue = new Bounds(center, size);
				return Ok();
			}
			return TypeError(prop, "Bounds {center:[x,y,z], size:[x,y,z]}");
		}

		private static Result<bool> WriteBoundsInt(SerializedProperty prop, JToken value)
		{
			if (value is JObject obj
				&& TryAsInts(obj["position"], 3, out var p)
				&& TryAsInts(obj["size"], 3, out var s))
			{
				prop.boundsIntValue = new BoundsInt(
					new Vector3Int(p[0], p[1], p[2]),
					new Vector3Int(s[0], s[1], s[2]));
				return Ok();
			}
			return TypeError(prop, "BoundsInt {position:[x,y,z], size:[x,y,z]}");
		}

		private static Result<bool> WriteEnum(SerializedProperty prop, JToken value)
		{
			var names = prop.enumNames;
			if (names == null || names.Length == 0)
				return Result<bool>.Error($"Enum '{prop.name}' has no values.");

			// Numeric: index directly.
			if (TryAsLong(value, out var idx))
			{
				if (idx < 0 || idx >= names.Length)
					return Result<bool>.Error($"Enum index {idx} out of range (0..{names.Length - 1}).");
				prop.enumValueIndex = (int)idx;
				return Ok();
			}

			var s = AsString(value);
			if (string.IsNullOrEmpty(s)) return TypeError(prop, "enum (name or index)");

			for (var i = 0; i < names.Length; i++)
				if (string.Equals(names[i], s, System.StringComparison.OrdinalIgnoreCase))
				{
					prop.enumValueIndex = i;
					return Ok();
				}

			return Result<bool>.Error(
				$"Enum '{prop.name}' has no value '{s}'. Valid: {string.Join(", ", names)}.");
		}

		private static Result<bool> WriteObjectReference(SerializedProperty prop, JToken value)
		{
			var typeHint = prop.managedReferenceFieldTypename;
			var objRes = ResolveObjectFromValue(value, typeHint);
			if (!objRes.IsSuccess) return Result<bool>.Error(objRes.ErrorMessage);
			prop.objectReferenceValue = objRes.Value;
			return Ok();
		}

		// ---- object resolution ----

		private static Result<Object> ResolveObjectFromValue(JToken value, string expected)
		{
			if (value == null || value.Type == JTokenType.Null)
				return Result<Object>.Success(null);

			// JObject form: { instanceId } or { path }, also { asset }
			if (value is JObject obj)
			{
				if (obj["instanceId"] != null && TryAsLong(obj["instanceId"], out var idTok))
					return ResolveByInstanceId((int)idTok);
				if (obj["path"] != null) return ResolveByString(obj["path"].ToString(), expected);
				if (obj["asset"] != null) return ResolveByString(obj["asset"].ToString(), expected);
				return Result<Object>.Error("Object reference object needs 'instanceId', 'path', or 'asset'.");
			}

			var s = AsString(value);
			if (string.IsNullOrEmpty(s)) return Result<Object>.Success(null);
			if (s == "null" || s == "none" || s == "None") return Result<Object>.Success(null);
			return ResolveByString(s, expected);
		}

		private static Result<Object> ResolveByInstanceId(int id)
		{
			var o = EditorUtility.InstanceIDToObject(id);
			if (o == null) return Result<Object>.Error($"No object with instance ID #{id}.");
			return Result<Object>.Success(o);
		}

		private static Result<Object> ResolveByString(string s, string expected)
		{
			if (s.Length > 0 && s[0] == '#')
			{
				if (!int.TryParse(s.Substring(1), out var id))
					return Result<Object>.Error($"Invalid instance ID '{s}'.");
				return ResolveByInstanceId(id);
			}

			// Asset path.
			if (s.StartsWith("Assets/", System.StringComparison.Ordinal) || s == "Assets")
			{
				var asset = AssetDatabase.LoadAssetAtPath<Object>(s);
				if (asset == null)
					return Result<Object>.Error($"No asset at '{s}'.");
				return Result<Object>.Success(asset);
			}

			// Scene path: resolve to GameObject; if expected is a Component subtype, fetch it.
			var parsed = PathParser.Parse(s);
			if (!parsed.IsSuccess) return Result<Object>.Error(parsed.ErrorMessage);
			var goRes = PathResolver.ResolveGameObject(parsed.Value);
			if (!goRes.IsSuccess) return Result<Object>.Error(goRes.ErrorMessage);

			// If the field expects a Component, prefer that component on the object.
			if (!string.IsNullOrEmpty(expected) && expected != "GameObject" && expected != "PPtr<GameObject>")
			{
				var typeName = expected;
				if (typeName.StartsWith("PPtr<", System.StringComparison.Ordinal) && typeName.EndsWith(">"))
					typeName = typeName.Substring(5, typeName.Length - 6).TrimStart('$');
				var compType = TypeResolver.ResolveComponentType(typeName);
				if (compType != null)
				{
					var comp = goRes.Value.GetComponent(compType);
					if (comp != null) return Result<Object>.Success(comp);
				}
			}

			return Result<Object>.Success(goRes.Value);
		}

		// ---- coercion helpers ----

		private static bool TryAsLong(JToken t, out long value)
		{
			value = 0;
			if (t == null) return false;
			if (t.Type == JTokenType.Integer) { value = t.Value<long>(); return true; }
			if (t.Type == JTokenType.Float) { value = (long)t.Value<double>(); return true; }
			if (t.Type == JTokenType.Boolean) { value = t.Value<bool>() ? 1 : 0; return true; }
			var s = t.ToString();
			return long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
		}

		private static bool TryAsFloat(JToken t, out float value)
		{
			value = 0;
			if (t == null) return false;
			if (t.Type == JTokenType.Float) { value = (float)t.Value<double>(); return true; }
			if (t.Type == JTokenType.Integer) { value = t.Value<long>(); return true; }
			var s = t.ToString();
			return float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
		}

		private static bool TryAsBool(JToken t, out bool value)
		{
			value = false;
			if (t == null) return false;
			if (t.Type == JTokenType.Boolean) { value = t.Value<bool>(); return true; }
			if (t.Type == JTokenType.Integer) { value = t.Value<long>() != 0; return true; }
			var s = t.ToString();
			if (bool.TryParse(s, out value)) return true;
			if (s == "1" || s.Equals("yes", System.StringComparison.OrdinalIgnoreCase)) { value = true; return true; }
			if (s == "0" || s.Equals("no", System.StringComparison.OrdinalIgnoreCase)) { value = false; return true; }
			return false;
		}

		private static string AsString(JToken t)
		{
			if (t == null || t.Type == JTokenType.Null) return null;
			return t.ToString();
		}

		private static bool TryAsFloats(JToken t, int count, out float[] result)
		{
			result = null;
			if (t == null) return false;

			if (t is JArray arr)
			{
				if (arr.Count != count) return false;
				result = new float[count];
				for (var i = 0; i < count; i++)
					if (!TryAsFloat(arr[i], out result[i])) return false;
				return true;
			}

			if (t is JObject obj)
			{
				string[] keys = count switch
				{
					2 => new[] { "x", "y" },
					3 => new[] { "x", "y", "z" },
					4 => new[] { "x", "y", "z", "w" },
					_ => null,
				};
				if (keys == null) return false;
				result = new float[count];
				for (var i = 0; i < count; i++)
					if (!TryAsFloat(obj[keys[i]], out result[i])) return false;
				return true;
			}

			var s = t.ToString();
			if (string.IsNullOrEmpty(s)) return false;
			s = s.Trim().TrimStart('(').TrimEnd(')');
			// Accept either comma- or whitespace-separated components so that
			// `get | set` round-trips work (get emits "x y z" by default).
			var parts = s.Split(new[] { ',', ' ', '\t', '\n', '\r' }, System.StringSplitOptions.RemoveEmptyEntries);
			if (parts.Length != count) return false;
			result = new float[count];
			for (var i = 0; i < count; i++)
				if (!float.TryParse(parts[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out result[i]))
					return false;
			return true;
		}

		private static bool TryAsInts(JToken t, int count, out int[] result)
		{
			result = null;
			if (!TryAsFloats(t, count, out var floats)) return false;
			result = new int[count];
			for (var i = 0; i < count; i++) result[i] = (int)floats[i];
			return true;
		}

		private static bool TryAsColor(JToken t, out Color color)
		{
			color = default;
			if (t == null) return false;

			if (t is JObject obj)
			{
				var r = obj["r"]; var g = obj["g"]; var b = obj["b"]; var a = obj["a"];
				if (r != null && g != null && b != null
					&& TryAsFloat(r, out var rf) && TryAsFloat(g, out var gf) && TryAsFloat(b, out var bf))
				{
					var af = 1f;
					if (a != null && !TryAsFloat(a, out af)) return false;
					color = new Color(rf, gf, bf, af);
					return true;
				}
			}

			if (t is JArray arr && (arr.Count == 3 || arr.Count == 4))
			{
				var rf = 0f; var gf = 0f; var bf = 0f; var af = 1f;
				if (!TryAsFloat(arr[0], out rf) || !TryAsFloat(arr[1], out gf) || !TryAsFloat(arr[2], out bf))
					return false;
				if (arr.Count == 4 && !TryAsFloat(arr[3], out af)) return false;
				color = new Color(rf, gf, bf, af);
				return true;
			}

			var s = t.ToString();
			if (string.IsNullOrEmpty(s)) return false;

			if (s[0] == '#')
				return ColorUtility.TryParseHtmlString(s, out color);

			// "r,g,b[,a]" or "r g b[ a]" — same round-trip rule as TryAsFloats.
			var parts = s.Split(new[] { ',', ' ', '\t', '\n', '\r' }, System.StringSplitOptions.RemoveEmptyEntries);
			if (parts.Length == 3 || parts.Length == 4)
			{
				var c = new float[4] { 0, 0, 0, 1 };
				for (var i = 0; i < parts.Length; i++)
					if (!float.TryParse(parts[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out c[i]))
						return false;
				color = new Color(c[0], c[1], c[2], c[3]);
				return true;
			}

			// Named CSS colors via ColorUtility.
			return ColorUtility.TryParseHtmlString(s, out color);
		}

		// ---- shorthand ----

		private static Result<bool> Ok() => Result<bool>.Success(true);

		private static Result<bool> TypeError(SerializedProperty prop, string expected)
			=> Result<bool>.Error($"Cannot assign to '{prop.name}': expected {expected}.");
	}
}
