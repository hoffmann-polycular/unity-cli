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
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace UnityCliConnector.Tools
{
	/// <summary>
	/// Calls a method on a component by reflection — the CLI-first way to drive
	/// an Odin <c>[Button]</c>, run a <c>Solve()</c>, or exercise any method
	/// while testing, without dropping to <c>exec</c>. Resolves the method by
	/// name at ANY access level (public/private/static — a private
	/// <c>[Button]</c> is just a private method), coerces CLI args to the
	/// parameter types (reusing <see cref="ValueCoercion"/>), invokes, and
	/// returns the formatted result (via <see cref="ReflectionMemberProxy.Format"/>).
	/// Fans out across selection like <c>set</c>, sharing one Undo group.
	/// </summary>
	[UnityCliTool(Name = "invoke", Group = "Editor",
		Description = "Call a method (public/private/static, incl. Odin [Button]) on a component by reflection.")]
	public static class InvokeMethod
	{
		public class Parameters
		{
			[ToolParameter("Target path incl. ':Component.MethodName' (e.g. /World/Safe:KnobCombination.Solve).", Required = true)]
			public string Path { get; set; }

			[ToolParameter("Method arguments, coerced to the parameter types (scalars, \"1 2 3\" vectors, enum names, object paths).")]
			public string Args { get; set; }

			[ToolParameter("Output format: human/plain (default) or json.")]
			public string Format { get; set; }
		}

		public static object HandleCommand(JObject @params)
		{
			var p = new ToolParams(@params);
			var argsArr = p.GetRaw("args") as JArray;
			var pathsArr = p.GetRaw("paths") as JArray;   // stdin fan-out (Go-side)
			var pathParam = p.Get("path");
			var format = (p.Get("format") ?? "plain").ToLowerInvariant();

			// Path is either the explicit param or the first positional; the
			// remaining positionals are the method arguments.
			string path;
			var argStart = 0;
			if (!string.IsNullOrWhiteSpace(pathParam)) { path = pathParam; argStart = 0; }
			else if (argsArr != null && argsArr.Count > 0) { path = argsArr[0]?.ToString(); argStart = 1; }
			else path = null;

			if (string.IsNullOrWhiteSpace(path))
				return new ErrorResponse(
					"invoke requires a path, e.g. '/World/Player:MyScript.DoThing [args...]'.", ErrorKind.Usage);

			var methodArgs = new List<JToken>();
			if (argsArr != null)
				for (var i = argStart; i < argsArr.Count; i++) methodArgs.Add(argsArr[i]);

			var parseRes = PathParser.Parse(path);
			if (!parseRes.IsSuccess) return ErrorResponse.FromResult(parseRes);
			var parsed = parseRes.Value;

			if (!parsed.Component.IsPresent)
				return new ErrorResponse(
					"invoke requires a component and method — add ':TypeName.MethodName'.", ErrorKind.Usage);
			if (parsed.Properties == null || parsed.Properties.Count == 0)
				return new ErrorResponse(
					"invoke requires a method name — add '.MethodName' after the component.", ErrorKind.Usage);
			if (parsed.Properties.Count > 1)
				return new ErrorResponse(
					$"invoke takes ':Component.Method', not a sub-path "
					+ $"('{PathResolver.JoinPropertyPath(parsed.Properties, parsed.Properties.Count)}').", ErrorKind.Usage);

			var methodName = parsed.Properties[0];
			if (methodName.EndsWith("()", StringComparison.Ordinal))
				methodName = methodName.Substring(0, methodName.Length - 2);

			var invoked = new List<object>();
			var errors = new List<string>();

			// Targets: stdin paths (`find … | invoke :Comp.Method`) resolve
			// independently; otherwise fan out across the parsed path / selection.
			List<GameObject> targets;
			if (pathsArr != null && pathsArr.Count > 0)
			{
				targets = new List<GameObject>();
				foreach (var tok in pathsArr)
				{
					var ps = tok?.ToString();
					if (string.IsNullOrWhiteSpace(ps)) continue;
					var pr = PathParser.Parse(ps);
					if (!pr.IsSuccess) { errors.Add($"{ps}: {pr.ErrorMessage}"); continue; }
					var gr = PathResolver.ResolveGameObject(pr.Value);
					if (!gr.IsSuccess) { errors.Add($"{ps}: {gr.ErrorMessage}"); continue; }
					targets.Add(gr.Value);
				}
			}
			else
			{
				var targetsRes = PathResolver.ResolveTargets(parsed);
				if (!targetsRes.IsSuccess) return ErrorResponse.FromResult(targetsRes);
				targets = targetsRes.Value;
			}

			// One Undo group for the whole fan-out (best-effort — see InvokeOn).
			var undoGroup = Undo.GetCurrentGroup();
			Undo.IncrementCurrentGroup();
			Undo.SetCurrentGroupName($"invoke {parsed.Component.TypeName}.{methodName}");

			foreach (var go in targets)
			{
				var compRes = PathResolver.ResolveComponent(go, parsed.Component);
				if (!compRes.IsSuccess)
				{
					errors.Add($"{PathResolver.GetCanonicalPath(go)}: {compRes.ErrorMessage}");
					continue;
				}

				var callRes = InvokeOn(compRes.Value, methodName, methodArgs);
				if (!callRes.IsSuccess)
				{
					errors.Add($"{PathResolver.GetCanonicalPath(go)}: {callRes.ErrorMessage}");
					continue;
				}
				invoked.Add(callRes.Value);
			}

			Undo.CollapseUndoOperations(undoGroup);

			if (invoked.Count == 0)
				return new ErrorResponse(
					errors.Count == 1 ? errors[0]
						: $"invoke failed for all {targets.Count} target(s):\n  " + string.Join("\n  ", errors),
					ErrorKind.Runtime);

			return Render(invoked, errors, format);
		}

		// ── one target ──────────────────────────────────────────────────────

		private static Result<Dictionary<string, object>> InvokeOn(
			Component component, string methodName, List<JToken> args)
		{
			var resolveRes = ResolveMethod(component.GetType(), methodName, args);
			if (!resolveRes.IsSuccess)
				return Result<Dictionary<string, object>>.Error(resolveRes.ErrorMessage, resolveRes.ErrorKind);
			var method = resolveRes.Value.Method;
			var argv = resolveRes.Value.Argv;

			// Best-effort undo/dirty: captures the target component's own state
			// (not side effects on other objects); skipped in play mode.
			var playing = Application.isPlaying;
			if (!playing)
				Undo.RegisterCompleteObjectUndo(component, $"invoke {component.GetType().Name}.{methodName}");

			object ret;
			try
			{
				ret = method.Invoke(method.IsStatic ? null : component, argv);
			}
			catch (Exception ex)
			{
				var inner = ex is TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : ex;
				return Result<Dictionary<string, object>>.Error($"'{methodName}' threw: {inner.Message}", ErrorKind.Runtime);
			}

			if (!playing) EditorUtility.SetDirty(component);

			var record = new Dictionary<string, object>
			{
				["path"] = PathResolver.GetCanonicalPath(component.gameObject),
				["component"] = component.GetType().Name,
				["method"] = methodName,
				["returnType"] = method.ReturnType == typeof(void) ? "void" : method.ReturnType.Name,
				["argCount"] = argv.Length,
			};
			if (method.ReturnType != typeof(void))
				record["value"] = ReflectionMemberProxy.Format(ret);
			return Result<Dictionary<string, object>>.Success(record);
		}

		// ── method resolution ───────────────────────────────────────────────

		private struct Resolved { public MethodInfo Method; public object[] Argv; }

		private static Result<Resolved> ResolveMethod(Type type, string name, List<JToken> args)
		{
			// Gather candidates by name across all access levels, walking the
			// base-type chain (private methods aren't inherited via reflection).
			var candidates = new List<MethodInfo>();
			var seen = new HashSet<string>();
			const BindingFlags bf = BindingFlags.Public | BindingFlags.NonPublic
				| BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
			for (var cur = type; cur != null && cur != typeof(object); cur = cur.BaseType)
			{
				foreach (var m in cur.GetMethods(bf))
				{
					if (m.IsSpecialName) continue;            // skip property/event accessors
					if (m.IsGenericMethodDefinition) continue; // can't invoke open generics
					if (!string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
					if (seen.Add(m.ToString())) candidates.Add(m);
				}
			}

			if (candidates.Count == 0)
				return Result<Resolved>.Error($"No method '{name}' on {type.Name}.", ErrorKind.NotFound);

			// Prefer exact-case matches when both cases exist.
			var exact = candidates.Where(m => m.Name == name).ToList();
			if (exact.Count > 0) candidates = exact;

			var arityMatches = candidates.Where(m => AcceptsArgCount(m, args.Count)).ToList();
			if (arityMatches.Count == 0)
			{
				var sigs = string.Join("\n  ", candidates.Select(Signature));
				return Result<Resolved>.Error(
					$"'{name}' on {type.Name} takes a different number of arguments (got {args.Count}). Overload(s):\n  {sigs}",
					ErrorKind.Usage);
			}

			var viable = new List<Resolved>();
			string lastErr = null;
			foreach (var m in arityMatches)
			{
				var built = BuildArgs(m, args);
				if (built.IsSuccess) viable.Add(new Resolved { Method = m, Argv = built.Value });
				else lastErr = built.ErrorMessage;
			}

			if (viable.Count == 0)
				return Result<Resolved>.Error(
					arityMatches.Count == 1 ? lastErr
						: $"No overload of '{name}' on {type.Name} accepts the given arguments. Last tried: {lastErr}",
					ErrorKind.Usage);
			if (viable.Count > 1)
			{
				var sigs = string.Join("\n  ", viable.Select(v => Signature(v.Method)));
				return Result<Resolved>.Error(
					$"Ambiguous call to '{name}' on {type.Name} — {viable.Count} overloads match:\n  {sigs}",
					ErrorKind.Ambiguous);
			}

			return Result<Resolved>.Success(viable[0]);
		}

		// arg count must land between the required params and the total (extra
		// optionals fill from their defaults). params-array methods are not
		// specially handled.
		private static bool AcceptsArgCount(MethodInfo m, int argCount)
		{
			var ps = m.GetParameters();
			var required = ps.Count(pp => !pp.HasDefaultValue);
			return argCount >= required && argCount <= ps.Length;
		}

		private static Result<object[]> BuildArgs(MethodInfo m, List<JToken> args)
		{
			var ps = m.GetParameters();
			var argv = new object[ps.Length];
			for (var i = 0; i < ps.Length; i++)
			{
				if (i < args.Count)
				{
					var coerced = ValueCoercion.Coerce(args[i], ps[i].ParameterType);
					if (!coerced.IsSuccess)
						return Result<object[]>.Error($"arg {i} ('{ps[i].Name}'): {coerced.ErrorMessage}");
					argv[i] = coerced.Value;
				}
				else if (ps[i].HasDefaultValue) argv[i] = ps[i].DefaultValue;
				else return Result<object[]>.Error($"missing required argument '{ps[i].Name}'.");
			}
			return Result<object[]>.Success(argv);
		}

		private static string Signature(MethodInfo m)
		{
			var ps = string.Join(", ", m.GetParameters().Select(pp => $"{FriendlyType(pp.ParameterType)} {pp.Name}"));
			var mods = (m.IsStatic ? "static " : "") + (m.IsPublic ? "" : "non-public ");
			return $"{mods}{FriendlyType(m.ReturnType)} {m.Name}({ps})";
		}

		private static string FriendlyType(Type t) => t == typeof(void) ? "void" : t.Name;

		// ── rendering ───────────────────────────────────────────────────────

		private static object Render(List<object> invoked, List<string> errors, string format)
		{
			if (format == "json")
			{
				if (invoked.Count == 1 && errors.Count == 0)
					return new SuccessResponse(Describe((Dictionary<string, object>)invoked[0]), invoked[0]);

				var msg = errors.Count == 0
					? $"invoked on {invoked.Count} object(s)."
					: $"invoked on {invoked.Count} object(s); {errors.Count} failed.";
				var resp = new SuccessResponse(msg, new Dictionary<string, object>
				{
					["invoked"] = invoked,
					["errors"] = errors,
				});
				if (errors.Count > 0) { resp.partialFailure = true; resp.stderr = string.Join("\n", errors); }
				return resp;
			}

			var lines = invoked.Select(e => Describe((Dictionary<string, object>)e)).ToList();
			var plainResp = new SuccessResponse(
				invoked.Count == 1 ? lines[0] : $"invoked on {invoked.Count} object(s).",
				string.Join("\n", lines));
			if (errors.Count > 0) { plainResp.partialFailure = true; plainResp.stderr = string.Join("\n", errors); }
			return plainResp;
		}

		private static string Describe(Dictionary<string, object> d)
		{
			var path = d.TryGetValue("path", out var pv) ? pv?.ToString() : "";
			var comp = d.TryGetValue("component", out var cv) ? cv?.ToString() : "";
			var method = d.TryGetValue("method", out var mv) ? mv?.ToString() : "";
			var head = $"{path}:{comp}.{method}()";
			if (d.TryGetValue("value", out var val))
				return $"{head} = {Get.FormatPipeFriendly(val)}";
			return $"{head} → void";
		}
	}
}
