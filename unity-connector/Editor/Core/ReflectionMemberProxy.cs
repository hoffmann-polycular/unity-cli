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
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UObject = UnityEngine.Object;

namespace UnityCliConnector
{
	/// <summary>
	/// Last-resort resolver for property names that have no backing
	/// <see cref="SerializedProperty"/>. Unity devs reach for the C# accessors
	/// they use in code — <c>transform.position</c>, <c>eulerAngles</c>,
	/// <c>forward</c>, or a custom MonoBehaviour's <c>IsSolved</c> — but those
	/// are computed properties with no serialized field, so the SerializedObject
	/// lookup in <see cref="PathResolver.FindPropertyByUserName"/> misses them.
	///
	/// This proxy is consulted only after that lookup returns null. It resolves
	/// the name as a public instance property/field via reflection and gets/sets
	/// it on the live object, following the same Undo/dirty pattern as
	/// <see cref="GameObjectProxy"/> (<c>Undo.RecordObject</c> → live set →
	/// <c>EditorUtility.SetDirty</c> → <c>RecordPrefabInstancePropertyModifications</c>).
	/// Writing through Unity's own C# setters also lets Unity do the derived-value
	/// math for us — e.g. <c>transform.position</c> applies the parent-inverse
	/// transform to update <c>localPosition</c> correctly. (The editor-only
	/// rotation "hint" that disambiguates Euler angles is not updated by the
	/// runtime setter; the inspector recomputes Euler from the quaternion, same
	/// as any assignment made from game code.)
	///
	/// Values are coerced from/to the exact same shapes as the SerializedProperty
	/// path (vectors → <c>{x,y,z}</c>, object refs → <c>{type,path,instanceId}</c>)
	/// so <c>get</c>'s pipe-friendly formatting works unchanged.
	/// </summary>
	public static class ReflectionMemberProxy
	{
		private const BindingFlags MemberFlags = BindingFlags.Public | BindingFlags.Instance;

		public class ReadResult
		{
			public object Value;
			public string TypeName;
		}

		// ── member resolution ───────────────────────────────────────────────

		/// <summary>
		/// Resolves <paramref name="name"/> to a public instance property or
		/// field on <paramref name="target"/>'s type. Exact-case matches win;
		/// otherwise the first case-insensitive match. Properties are preferred
		/// over fields, and indexers are skipped. This gates the fallback: the
		/// callers only fall through to the improved "no member" error when this
		/// returns false.
		/// </summary>
		internal static bool TryResolveMember(object target, string name, out MemberInfo member)
		{
			member = null;
			if (target == null || string.IsNullOrEmpty(name)) return false;
			var t = target.GetType();

			foreach (var pi in t.GetProperties(MemberFlags))
				if (pi.GetIndexParameters().Length == 0 && pi.Name == name) { member = pi; return true; }
			foreach (var fi in t.GetFields(MemberFlags))
				if (fi.Name == name) { member = fi; return true; }

			foreach (var pi in t.GetProperties(MemberFlags))
				if (pi.GetIndexParameters().Length == 0
					&& string.Equals(pi.Name, name, StringComparison.OrdinalIgnoreCase))
				{ member = pi; return true; }
			foreach (var fi in t.GetFields(MemberFlags))
				if (string.Equals(fi.Name, name, StringComparison.OrdinalIgnoreCase))
				{ member = fi; return true; }

			return false;
		}

		// ── read ────────────────────────────────────────────────────────────

		public static Result<ReadResult> Read(Component c, List<string> segments)
		{
			if (c == null || segments == null || segments.Count == 0)
				return Result<ReadResult>.Error("Nothing to read.");

			if (!TryResolveMember(c, segments[0], out var member))
				return Result<ReadResult>.Error(
					$"No serialized property or public C# member '{segments[0]}' on {c.GetType().Name}.",
					ErrorKind.NotFound);

			if (!CanRead(member))
				return Result<ReadResult>.Error($"C# member '{segments[0]}' is write-only.", ErrorKind.Usage);

			object raw;
			try { raw = GetValue(member, c); }
			catch (Exception ex)
			{
				return Result<ReadResult>.Error($"Reading '{segments[0]}' threw: {Unwrap(ex).Message}");
			}

			var value = Format(raw);
			for (var i = 1; i < segments.Count; i++)
			{
				var stepRes = IndexInto(value, segments[i], PathResolver.JoinPropertyPath(segments, i));
				if (!stepRes.IsSuccess) return Result<ReadResult>.Error(stepRes.ErrorMessage, ErrorKind.NotFound);
				value = stepRes.Value;
			}

			return Result<ReadResult>.Success(new ReadResult
			{
				Value = value,
				TypeName = GetMemberType(member).Name,
			});
		}

		// ── set ─────────────────────────────────────────────────────────────

		public static Result<Dictionary<string, object>> Set(Component c, List<string> segments, JToken rawValue)
		{
			if (c == null || segments == null || segments.Count == 0)
				return Result<Dictionary<string, object>>.Error("Nothing to set.");

			if (!TryResolveMember(c, segments[0], out var member))
				return Result<Dictionary<string, object>>.Error(
					$"No serialized property or public C# member '{segments[0]}' on {c.GetType().Name}.",
					ErrorKind.NotFound);

			if (segments.Count > 2)
				return Result<Dictionary<string, object>>.Error(
					"Nested sub-field set beyond one level isn't supported via reflection " +
					$"(got '{PathResolver.JoinPropertyPath(segments, segments.Count)}').", ErrorKind.Usage);

			var memberType = GetMemberType(member);
			var oldValue = CanRead(member) ? TryFormatCurrent(member, c) : null;

			var propName = PathResolver.JoinPropertyPath(segments, segments.Count);
			Undo.RecordObject(c, $"set {c.GetType().Name}.{propName}");

			if (segments.Count == 1)
			{
				if (!CanWrite(member))
					return Result<Dictionary<string, object>>.Error(
						$"C# member '{segments[0]}' is read-only.", ErrorKind.Usage);

				var coerced = ValueCoercion.Coerce(rawValue, memberType);
				if (!coerced.IsSuccess)
					return Result<Dictionary<string, object>>.Error(coerced.ErrorMessage);

				try { SetValue(member, c, coerced.Value); }
				catch (Exception ex)
				{
					return Result<Dictionary<string, object>>.Error($"Setting '{segments[0]}' threw: {Unwrap(ex).Message}");
				}
			}
			else // segments.Count == 2 → read-modify-write a struct sub-field
			{
				if (!CanRead(member))
					return Result<Dictionary<string, object>>.Error(
						$"Cannot set a sub-field of write-only member '{segments[0]}'.", ErrorKind.Usage);

				object container;
				try { container = GetValue(member, c); }
				catch (Exception ex)
				{
					return Result<Dictionary<string, object>>.Error($"Reading '{segments[0]}' threw: {Unwrap(ex).Message}");
				}
				if (container == null)
					return Result<Dictionary<string, object>>.Error(
						$"Member '{segments[0]}' is null; cannot set sub-field '{segments[1]}'.");

				if (!TryResolveMember(container, segments[1], out var sub))
					return Result<Dictionary<string, object>>.Error(
						$"No sub-field '{segments[1]}' on {memberType.Name}.", ErrorKind.NotFound);
				if (!CanWrite(sub))
					return Result<Dictionary<string, object>>.Error(
						$"Sub-field '{segments[1]}' is read-only.", ErrorKind.Usage);

				var coerced = ValueCoercion.Coerce(rawValue, GetMemberType(sub));
				if (!coerced.IsSuccess)
					return Result<Dictionary<string, object>>.Error(coerced.ErrorMessage);

				try
				{
					SetValue(sub, container, coerced.Value); // mutates the boxed struct
					if (!CanWrite(member))
						return Result<Dictionary<string, object>>.Error(
							$"C# member '{segments[0]}' is read-only; cannot write back sub-field.", ErrorKind.Usage);
					SetValue(member, c, container);          // write the struct back
				}
				catch (Exception ex)
				{
					return Result<Dictionary<string, object>>.Error($"Setting '{propName}' threw: {Unwrap(ex).Message}");
				}
			}

			EditorUtility.SetDirty(c);
			// A direct C# mutation doesn't flow through SerializedObject.
			// ApplyModifiedProperties, so on a prefab instance it wouldn't be
			// registered as an override. Record it explicitly (no-op off-instance)
			// so `prefab diff`/`apply`/`revert` see the change.
			PrefabUtility.RecordPrefabInstancePropertyModifications(c);
			var newValue = CanRead(member) ? TryFormatCurrent(member, c) : null;

			return Result<Dictionary<string, object>>.Success(new Dictionary<string, object>
			{
				["path"] = PathResolver.GetCanonicalPath(c.gameObject),
				["component"] = c.GetType().Name,
				["property"] = propName,
				["type"] = memberType.Name,
				["oldValue"] = oldValue,
				["newValue"] = newValue,
				["override"] = false,
			});
		}

		// ── helpers ─────────────────────────────────────────────────────────

		private static object TryFormatCurrent(MemberInfo member, object obj)
		{
			try { return Format(GetValue(member, obj)); }
			catch { return null; }
		}

		private static Result<object> IndexInto(object value, string segment, string traversed)
		{
			if (segment.Length >= 2 && segment[0] == '[' && segment[segment.Length - 1] == ']')
			{
				var idxStr = segment.Substring(1, segment.Length - 2);
				if (!int.TryParse(idxStr, out var idx) || idx < 0)
					return Result<object>.Error($"Invalid index '{segment}' under '{traversed}'.");
				if (value is List<object> list)
				{
					if (idx >= list.Count)
						return Result<object>.Error($"Index {idx} out of range under '{traversed}'.");
					return Result<object>.Success(list[idx]);
				}
				return Result<object>.Error($"'{traversed}' is not indexable.");
			}

			if (value is Dictionary<string, object> dict)
			{
				foreach (var kv in dict)
					if (string.Equals(kv.Key, segment, StringComparison.OrdinalIgnoreCase))
						return Result<object>.Success(kv.Value);
				return Result<object>.Error($"No sub-field '{segment}' under '{traversed}'.");
			}

			return Result<object>.Error($"Cannot read sub-field '{segment}': '{traversed}' is a scalar.");
		}

		private static bool CanRead(MemberInfo m)
			=> m is FieldInfo || (m is PropertyInfo p && p.CanRead);

		private static bool CanWrite(MemberInfo m)
			=> (m is FieldInfo f && !f.IsInitOnly && !f.IsLiteral) || (m is PropertyInfo p && p.CanWrite);

		private static Type GetMemberType(MemberInfo m)
			=> m is FieldInfo f ? f.FieldType : ((PropertyInfo)m).PropertyType;

		private static object GetValue(MemberInfo m, object obj)
			=> m is FieldInfo f ? f.GetValue(obj) : ((PropertyInfo)m).GetValue(obj);

		private static void SetValue(MemberInfo m, object obj, object val)
		{
			if (m is FieldInfo f) f.SetValue(obj, val);
			else ((PropertyInfo)m).SetValue(obj, val);
		}

		private static Exception Unwrap(Exception ex)
			=> ex is TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : ex;

		// ── value formatting (CLR → same shapes as SerializedPropertyReader) ──

		internal static object Format(object v)
		{
			switch (v)
			{
				case null: return null;
				case string s: return s;
				case bool b: return b;
				case char c: return c.ToString();
			}

			var t = v.GetType();
			if (t.IsEnum) return v.ToString();

			switch (v)
			{
				case Vector2 v2: return new Dictionary<string, object> { ["x"] = v2.x, ["y"] = v2.y };
				case Vector3 v3: return new Dictionary<string, object> { ["x"] = v3.x, ["y"] = v3.y, ["z"] = v3.z };
				case Vector4 v4: return new Dictionary<string, object> { ["x"] = v4.x, ["y"] = v4.y, ["z"] = v4.z, ["w"] = v4.w };
				case Vector2Int v2i: return new Dictionary<string, object> { ["x"] = v2i.x, ["y"] = v2i.y };
				case Vector3Int v3i: return new Dictionary<string, object> { ["x"] = v3i.x, ["y"] = v3i.y, ["z"] = v3i.z };
				case Quaternion q: return new Dictionary<string, object> { ["x"] = q.x, ["y"] = q.y, ["z"] = q.z, ["w"] = q.w };
				case Color col: return new Dictionary<string, object> { ["r"] = col.r, ["g"] = col.g, ["b"] = col.b, ["a"] = col.a };
				case Color32 c32: return new Dictionary<string, object> { ["r"] = (int)c32.r, ["g"] = (int)c32.g, ["b"] = (int)c32.b, ["a"] = (int)c32.a };
				case Rect r: return new Dictionary<string, object> { ["x"] = r.x, ["y"] = r.y, ["width"] = r.width, ["height"] = r.height };
				case Bounds bnd:
					return new Dictionary<string, object>
					{
						["center"] = new Dictionary<string, object> { ["x"] = bnd.center.x, ["y"] = bnd.center.y, ["z"] = bnd.center.z },
						["extents"] = new Dictionary<string, object> { ["x"] = bnd.extents.x, ["y"] = bnd.extents.y, ["z"] = bnd.extents.z },
					};
				case UObject uo: return SerializedPropertyReader.ReadObjectReference(uo);
				case decimal dec: return (double)dec;
			}

			if (t.IsPrimitive) return v; // int/long/float/double/byte/… serialize fine as-is
			if (v is IEnumerable en)
			{
				var list = new List<object>();
				foreach (var e in en) list.Add(Format(e));
				return list;
			}
			return v.ToString();
		}
	}

	/// <summary>
	/// Coerces a CLI/JSON <see cref="JToken"/> into an arbitrary C# type for the
	/// reflection set-path. Reuses <see cref="SerializedPropertyWriter"/>'s
	/// battle-tested string/number/vector/color/object parsers so the accepted
	/// input syntax matches the SerializedProperty path exactly.
	/// </summary>
	internal static class ValueCoercion
	{
		internal static Result<object> Coerce(JToken value, Type target)
		{
			var underlying = Nullable.GetUnderlyingType(target);
			if (underlying != null)
			{
				if (value == null || value.Type == JTokenType.Null) return Result<object>.Success(null);
				target = underlying;
			}

			if (target == typeof(string))
				return Result<object>.Success(SerializedPropertyWriter.AsString(value));

			if (target == typeof(bool))
				return SerializedPropertyWriter.TryAsBool(value, out var b)
					? Result<object>.Success(b) : Err(target);

			if (target == typeof(char))
			{
				var s = SerializedPropertyWriter.AsString(value);
				if (string.IsNullOrEmpty(s)) return Err(target);
				return Result<object>.Success(s[0]);
			}

			if (target.IsEnum)
			{
				if (SerializedPropertyWriter.TryAsLong(value, out var l))
					return Result<object>.Success(Enum.ToObject(target, l));
				var s = SerializedPropertyWriter.AsString(value);
				if (!string.IsNullOrEmpty(s))
				{
					try { return Result<object>.Success(Enum.Parse(target, s, ignoreCase: true)); }
					catch
					{
						return Result<object>.Error(
							$"'{s}' is not a valid {target.Name}. Valid: {string.Join(", ", Enum.GetNames(target))}.");
					}
				}
				return Err(target);
			}

			if (target == typeof(int) || target == typeof(uint) || target == typeof(long) || target == typeof(ulong)
				|| target == typeof(short) || target == typeof(ushort) || target == typeof(byte) || target == typeof(sbyte))
			{
				if (!SerializedPropertyWriter.TryAsLong(value, out var l)) return Err(target);
				try { return Result<object>.Success(Convert.ChangeType(l, target, CultureInfo.InvariantCulture)); }
				catch { return Err(target); }
			}

			if (target == typeof(float))
				return SerializedPropertyWriter.TryAsFloat(value, out var f)
					? Result<object>.Success(f) : Err(target);

			if (target == typeof(double) || target == typeof(decimal))
			{
				var s = SerializedPropertyWriter.AsString(value);
				if (s != null && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
					return Result<object>.Success(target == typeof(decimal) ? (object)(decimal)d : d);
				return Err(target);
			}

			if (target == typeof(Vector2))
				return SerializedPropertyWriter.TryAsFloats(value, 2, out var a2)
					? Result<object>.Success(new Vector2(a2[0], a2[1])) : Err(target);
			if (target == typeof(Vector3))
				return SerializedPropertyWriter.TryAsFloats(value, 3, out var a3)
					? Result<object>.Success(new Vector3(a3[0], a3[1], a3[2])) : Err(target);
			if (target == typeof(Vector4))
				return SerializedPropertyWriter.TryAsFloats(value, 4, out var a4)
					? Result<object>.Success(new Vector4(a4[0], a4[1], a4[2], a4[3])) : Err(target);
			if (target == typeof(Vector2Int))
				return SerializedPropertyWriter.TryAsInts(value, 2, out var i2)
					? Result<object>.Success(new Vector2Int(i2[0], i2[1])) : Err(target);
			if (target == typeof(Vector3Int))
				return SerializedPropertyWriter.TryAsInts(value, 3, out var i3)
					? Result<object>.Success(new Vector3Int(i3[0], i3[1], i3[2])) : Err(target);

			if (target == typeof(Quaternion))
				return CoerceQuaternion(value);

			if (target == typeof(Color))
				return SerializedPropertyWriter.TryAsColor(value, out var col)
					? Result<object>.Success(col) : Err(target);
			if (target == typeof(Color32))
				return SerializedPropertyWriter.TryAsColor(value, out var col32)
					? Result<object>.Success((Color32)col32) : Err(target);

			if (target == typeof(Rect))
				return SerializedPropertyWriter.TryAsFloats(value, 4, out var r4)
					? Result<object>.Success(new Rect(r4[0], r4[1], r4[2], r4[3])) : Err(target);

			if (typeof(UObject).IsAssignableFrom(target))
			{
				var res = SerializedPropertyWriter.ResolveObjectFromValue(value, target.Name);
				if (!res.IsSuccess) return Result<object>.Error(res.ErrorMessage);
				var obj = res.Value;
				if (obj != null && !target.IsInstanceOfType(obj))
					return Result<object>.Error(
						$"'{obj.name}' is a {obj.GetType().Name}, not assignable to {target.Name}.");
				return Result<object>.Success(obj);
			}

			return Result<object>.Error(
				$"Setting a C# member of type {target.Name} via reflection isn't supported " +
				"(set a sub-field instead, e.g. '.x').");
		}

		private static Result<object> CoerceQuaternion(JToken value)
		{
			if (value is JObject obj && obj["eulerAngles"] != null
				&& SerializedPropertyWriter.TryAsFloats(obj["eulerAngles"], 3, out var eu))
				return Result<object>.Success(Quaternion.Euler(eu[0], eu[1], eu[2]));
			if (SerializedPropertyWriter.TryAsFloats(value, 4, out var q4))
				return Result<object>.Success(new Quaternion(q4[0], q4[1], q4[2], q4[3]));
			if (SerializedPropertyWriter.TryAsFloats(value, 3, out var e3))
				return Result<object>.Success(Quaternion.Euler(e3[0], e3[1], e3[2]));
			return Result<object>.Error("Expected a Quaternion (3 Euler degrees or 4 raw components).");
		}

		private static Result<object> Err(Type target)
			=> Result<object>.Error($"Cannot coerce value to {target.Name}.");
	}
}
