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
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityCliConnector.InputSimulation;

namespace UnityCliConnector.Tools
{
	/// <summary>
	/// Real pointer and keyboard input for a game in play mode, injected as
	/// Input System device events so the game's own input handling (UI module,
	/// Mouse.current polling, raycast blocking, interactable state) decides what
	/// happens. Nothing here calls game code directly.
	///
	/// Coordinates are Game View pixels with a top-left origin, matching
	/// <c>unity-cli screenshot</c> at supersize 1. Every pointer action prints
	/// <c>x,y&lt;TAB&gt;kind&lt;TAB&gt;path</c> for what lies under the pointer
	/// (kind: ui, 3d, 2d or none), so a caller can tell whether a tap landed on
	/// its intended target or on something covering it.
	/// </summary>
	[UnityCliTool(Name = "input",
		Description = "Simulate real mouse/keyboard input in play mode. Actions: tap, drag, down, move, up, scroll, key, type, pick, reset.")]
	public static class InputTool
	{
		public class Parameters
		{
			[ToolParameter("Action: tap, drag, down, move, up, scroll, key, type, pick, or reset", Required = true)]
			public string Action { get; set; }

			[ToolParameter("Mouse button for tap/drag/down/up: left (default), right, middle.")]
			public string Button { get; set; }

			[ToolParameter("Frames to hold a tap or key press before releasing (default 1).")]
			public int HoldFrames { get; set; }

			[ToolParameter("Intermediate pointer moves for drag (default 10, one per frame).")]
			public int Steps { get; set; }

			[ToolParameter("Camera that projects 3D/2D targets and pick raycasts. Default: Camera.main.")]
			public string Camera { get; set; }
		}

		static readonly Regex s_PixelTarget = new Regex(@"^\s*(-?\d+(?:\.\d+)?)\s*,\s*(-?\d+(?:\.\d+)?)\s*$");

		public static async Task<object> HandleCommand(JObject parameters)
		{
			var p = new ToolParams(parameters);
			var args = (p.GetRaw("args") as JArray)?.Select(a => a?.ToString()).ToList() ?? new List<string>();
			var action = (p.Get("action") ?? args.FirstOrDefault())?.ToLowerInvariant();
			if (p.Get("action") == null && args.Count > 0)
				args.RemoveAt(0);
			if (string.IsNullOrEmpty(action))
				return ErrorResponse.Usage("Action required: tap, drag, down, move, up, scroll, key, type, pick, reset.");

			if (action == "reset")
			{
				if (SimulatedInput.NotReadyReason() == null)
					await SimulatedInput.ReleaseAll(1);
				SimulatedInput.Reset();
				return new SuccessResponse("Released simulated input and restored InputSettings.");
			}

			var notReady = SimulatedInput.NotReadyReason();
			if (notReady != null)
				return new ErrorResponse(notReady);

			var cameraRes = ResolveCamera(p.Get("camera"));
			if (!cameraRes.IsSuccess)
				return ErrorResponse.FromResult(cameraRes);
			var camera = cameraRes.Value;

			try
			{
				switch (action)
				{
					case "tap": return await Tap(args, p, camera);
					case "drag": return await Drag(args, p, camera);
					case "down": return await Press(args, p, camera, true);
					case "up": return await Press(args, p, camera, false);
					case "move": return await Move(args, camera);
					case "scroll": return await Scroll(args, camera);
					case "key": return await KeyCombo(args, p);
					case "type": return await TypeText(args);
					case "pick": return Pick(args, camera);
					default:
						return ErrorResponse.Usage($"Unknown action: '{action}'. Valid: tap, drag, down, move, up, scroll, key, type, pick, reset.");
				}
			}
			catch (Exception ex)
			{
				// A failure between press and release must not leave a button or
				// key held: every later tap would turn into a drag.
				_ = SimulatedInput.ReleaseAll(0);
				return new ErrorResponse((ex as TargetInvocationException)?.InnerException?.Message ?? ex.Message);
			}
		}

		static async Task<object> Tap(List<string> args, ToolParams p, Camera camera)
		{
			if (args.Count != 1) return ErrorResponse.Usage("Usage: input tap <x,y | path>");
			var buttonRes = ParseButton(p.Get("button"));
			if (!buttonRes.IsSuccess) return ErrorResponse.FromResult(buttonRes);
			var posRes = ResolveTarget(args[0], camera);
			if (!posRes.IsSuccess) return ErrorResponse.FromResult(posRes);

			var pos = posRes.Value;
			var report = Describe(pos, camera);
			await SimulatedInput.QueueMouse(pos, null, false, Vector2.zero, 1);
			await SimulatedInput.QueueMouse(pos, buttonRes.Value, true, Vector2.zero, HoldFrames(p));
			await SimulatedInput.QueueMouse(pos, buttonRes.Value, false, Vector2.zero, 1);
			return new SuccessResponse("", report);
		}

		static async Task<object> Drag(List<string> args, ToolParams p, Camera camera)
		{
			if (args.Count != 2) return ErrorResponse.Usage("Usage: input drag <from> <to>");
			var buttonRes = ParseButton(p.Get("button"));
			if (!buttonRes.IsSuccess) return ErrorResponse.FromResult(buttonRes);
			var fromRes = ResolveTarget(args[0], camera);
			if (!fromRes.IsSuccess) return ErrorResponse.FromResult(fromRes);
			var toRes = ResolveTarget(args[1], camera);
			if (!toRes.IsSuccess) return ErrorResponse.FromResult(toRes);

			var from = fromRes.Value;
			var to = toRes.Value;
			var steps = Math.Max(1, p.GetInt("steps", 10).Value);
			var fromReport = Describe(from, camera);
			await SimulatedInput.QueueMouse(from, null, false, Vector2.zero, 1);
			await SimulatedInput.QueueMouse(from, buttonRes.Value, true, Vector2.zero, 1);
			for (var i = 1; i <= steps; i++)
				await SimulatedInput.QueueMouse(Vector2.Lerp(from, to, (float)i / steps), null, false, Vector2.zero, 1);
			var toReport = Describe(to, camera);
			await SimulatedInput.QueueMouse(to, buttonRes.Value, false, Vector2.zero, 1);
			return new SuccessResponse("", fromReport + "\n" + toReport);
		}

		/// <summary>down/up at an optional target; without one they act where the pointer already is.</summary>
		static async Task<object> Press(List<string> args, ToolParams p, Camera camera, bool pressed)
		{
			if (args.Count > 1) return ErrorResponse.Usage($"Usage: input {(pressed ? "down" : "up")} [<x,y | path>]");
			var buttonRes = ParseButton(p.Get("button"));
			if (!buttonRes.IsSuccess) return ErrorResponse.FromResult(buttonRes);

			var pos = SimulatedInput.MousePosition;
			if (args.Count == 1)
			{
				var posRes = ResolveTarget(args[0], camera);
				if (!posRes.IsSuccess) return ErrorResponse.FromResult(posRes);
				pos = posRes.Value;
				await SimulatedInput.QueueMouse(pos, null, false, Vector2.zero, 1);
			}
			var report = Describe(pos, camera);
			await SimulatedInput.QueueMouse(pos, buttonRes.Value, pressed, Vector2.zero, 1);
			return new SuccessResponse("", report);
		}

		static async Task<object> Move(List<string> args, Camera camera)
		{
			if (args.Count != 1) return ErrorResponse.Usage("Usage: input move <x,y | path>");
			var posRes = ResolveTarget(args[0], camera);
			if (!posRes.IsSuccess) return ErrorResponse.FromResult(posRes);
			await SimulatedInput.QueueMouse(posRes.Value, null, false, Vector2.zero, 1);
			return new SuccessResponse("", Describe(posRes.Value, camera));
		}

		static async Task<object> Scroll(List<string> args, Camera camera)
		{
			if (args.Count != 2 && args.Count != 3)
				return ErrorResponse.Usage("Usage: input scroll <x,y | path> <dy> [<dx>]");
			var posRes = ResolveTarget(args[0], camera);
			if (!posRes.IsSuccess) return ErrorResponse.FromResult(posRes);
			if (!TryParseFloat(args[1], out var dy) || (args.Count == 3 && !TryParseFloat(args[2], out _)))
				return ErrorResponse.Usage("Scroll amounts must be numbers (raw Mouse.scroll units).");
			var dx = args.Count == 3 ? float.Parse(args[2], CultureInfo.InvariantCulture) : 0f;

			var pos = posRes.Value;
			await SimulatedInput.QueueMouse(pos, null, false, Vector2.zero, 1);
			await SimulatedInput.QueueMouse(pos, null, false, new Vector2(dx, dy), 1);
			return new SuccessResponse("", Describe(pos, camera));
		}

		static async Task<object> KeyCombo(List<string> args, ToolParams p)
		{
			if (args.Count != 1) return ErrorResponse.Usage("Usage: input key <Key>[+<Key>...]  (Input System Key names, e.g. Escape, Space, LeftCtrl+S)");
			var keys = new List<Key>();
			foreach (var name in args[0].Split('+'))
			{
				if (!Enum.TryParse(name.Trim(), true, out Key key) || key == Key.None || !Enum.IsDefined(typeof(Key), key))
					return ErrorResponse.Usage($"Unknown key '{name}'. Use UnityEngine.InputSystem.Key names (A, Digit1, Enter, Escape, LeftArrow, LeftCtrl, ...).");
				keys.Add(key);
			}

			var field = SimulatedInput.FocusedTextField();
			await SimulatedInput.QueueKeys(keys, HoldFrames(p));
			if (field != null && TryFieldEvent(keys, out var evt))
				SimulatedInput.SendToTextField(field, evt);
			await SimulatedInput.QueueKeys(Array.Empty<Key>(), 1);
			return new SuccessResponse("", field != null ? $"{args[0]}\tfield\t{PathResolver.GetCanonicalPath(field.gameObject)}" : args[0]);
		}

		static async Task<object> TypeText(List<string> args)
		{
			if (args.Count == 0) return ErrorResponse.Usage("Usage: input type <text>");
			var text = string.Join(" ", args);
			var field = SimulatedInput.FocusedTextField();
			await SimulatedInput.QueueText(text);
			if (field != null)
				foreach (var c in text)
					SimulatedInput.SendToTextField(field, new Event { type = EventType.KeyDown, character = c });
			return new SuccessResponse("", field != null ? $"field\t{PathResolver.GetCanonicalPath(field.gameObject)}" : "keyboard");
		}

		static object Pick(List<string> args, Camera camera)
		{
			if (args.Count != 1) return ErrorResponse.Usage("Usage: input pick <x,y | path>");
			var posRes = ResolveTarget(args[0], camera);
			if (!posRes.IsSuccess) return ErrorResponse.FromResult(posRes);
			return new SuccessResponse("", Describe(posRes.Value, camera));
		}

		static int HoldFrames(ToolParams p) => Math.Max(1, p.GetInt("hold_frames", 1).Value);

		static Result<MouseButton> ParseButton(string name)
		{
			switch ((name ?? "left").ToLowerInvariant())
			{
				case "left": return Result<MouseButton>.Success(MouseButton.Left);
				case "right": return Result<MouseButton>.Success(MouseButton.Right);
				case "middle": return Result<MouseButton>.Success(MouseButton.Middle);
				default: return Result<MouseButton>.Error($"Unknown button '{name}'. Valid: left, right, middle.", ErrorKind.Usage);
			}
		}

		static Result<Camera> ResolveCamera(string path)
		{
			if (string.IsNullOrEmpty(path))
				return Result<Camera>.Success(UnityEngine.Camera.main);
			var goRes = ResolvePath(path);
			if (!goRes.IsSuccess) return Result<Camera>.Error(goRes.ErrorMessage, goRes.ErrorKind);
			var camera = goRes.Value.GetComponent<Camera>();
			return camera != null
				? Result<Camera>.Success(camera)
				: Result<Camera>.Error($"{path} has no Camera component.", ErrorKind.NotFound);
		}

		static Result<GameObject> ResolvePath(string path)
		{
			var parsed = PathParser.Parse(path);
			if (!parsed.IsSuccess) return Result<GameObject>.Error(parsed.ErrorMessage, parsed.ErrorKind);
			return PathResolver.ResolveGameObject(parsed.Value);
		}

		/// <summary>Resolves a target to Unity screen space (bottom-left origin).</summary>
		static Result<Vector2> ResolveTarget(string target, Camera camera)
		{
			Vector2 pos;
			var pixel = s_PixelTarget.Match(target);
			if (pixel.Success)
			{
				var x = float.Parse(pixel.Groups[1].Value, CultureInfo.InvariantCulture);
				var y = float.Parse(pixel.Groups[2].Value, CultureInfo.InvariantCulture);
				pos = new Vector2(x, Screen.height - 1 - y);
			}
			else
			{
				var goRes = ResolvePath(target);
				if (!goRes.IsSuccess) return Result<Vector2>.Error(goRes.ErrorMessage, goRes.ErrorKind);
				var go = goRes.Value;
				var canvas = go.GetComponentInParent<Canvas>();
				if (go.transform is RectTransform rect && canvas != null)
				{
					var root = canvas.rootCanvas;
					// uGUI falls back to Camera.main for a camera-less world-space canvas.
					var uiCamera = root.renderMode == RenderMode.ScreenSpaceOverlay ? null
						: root.worldCamera != null ? root.worldCamera : camera;
					pos = RectTransformUtility.WorldToScreenPoint(uiCamera, rect.TransformPoint(rect.rect.center));
				}
				else
				{
					if (camera == null)
						return Result<Vector2>.Error("No camera to project the target with: no Camera.main; pass --camera <path>.", ErrorKind.Usage);
					var screen = camera.WorldToScreenPoint(WorldCenter(go));
					if (screen.z < 0)
						return Result<Vector2>.Error($"{target} is behind the camera.", ErrorKind.Usage);
					pos = screen;
				}
			}

			if (pos.x < 0 || pos.y < 0 || pos.x >= Screen.width || pos.y >= Screen.height)
				return Result<Vector2>.Error($"{target} is off screen at {Format(pos)} (Game View is {Screen.width}x{Screen.height}).", ErrorKind.Usage);
			return Result<Vector2>.Success(pos);
		}

		static Vector3 WorldCenter(GameObject go)
		{
#if UNITY_CLI_PHYSICS
			if (go.TryGetComponent<Collider>(out var collider)) return collider.bounds.center;
#endif
#if UNITY_CLI_PHYSICS2D
			if (go.TryGetComponent<Collider2D>(out var collider2D)) return collider2D.bounds.center;
#endif
			if (go.TryGetComponent<Renderer>(out var renderer)) return renderer.bounds.center;
			return go.transform.position;
		}

		/// <summary>What the game would hit at <paramref name="pos"/>: topmost UI first, then 3D, then 2D physics.</summary>
		static string Describe(Vector2 pos, Camera camera)
		{
			var eventSystem = EventSystem.current;
			if (eventSystem != null)
			{
				var results = new List<RaycastResult>();
				eventSystem.RaycastAll(new PointerEventData(eventSystem) { position = pos }, results);
				if (results.Count > 0)
					return $"{Format(pos)}\t{HitKind(results[0])}\t{PathResolver.GetCanonicalPath(results[0].gameObject)}";
			}
			if (camera != null)
			{
				var ray = camera.ScreenPointToRay(pos);
#if UNITY_CLI_PHYSICS
				if (Physics.Raycast(ray, out var hit))
					return $"{Format(pos)}\t3d\t{PathResolver.GetCanonicalPath(hit.collider.gameObject)}";
#endif
#if UNITY_CLI_PHYSICS2D
				var hit2D = Physics2D.GetRayIntersection(ray);
				if (hit2D.collider != null)
					return $"{Format(pos)}\t2d\t{PathResolver.GetCanonicalPath(hit2D.collider.gameObject)}";
#endif
			}
			return $"{Format(pos)}\tnone\t-";
		}

		/// <summary>EventSystem raycasts include Physics(2D)Raycasters, so a result is not necessarily UI.</summary>
		static string HitKind(RaycastResult result)
		{
			if (result.module is UnityEngine.UI.GraphicRaycaster)
				return "ui";
#if UNITY_CLI_PHYSICS2D
			if (result.gameObject.TryGetComponent<Collider2D>(out _))
				return "2d";
#endif
#if UNITY_CLI_PHYSICS
			if (result.gameObject.TryGetComponent<Collider>(out _))
				return "3d";
#endif
			return "ui";
		}

		static string Format(Vector2 unityScreen)
			=> $"{Mathf.RoundToInt(unityScreen.x)},{Mathf.RoundToInt(Screen.height - 1 - unityScreen.y)}";

		static bool TryParseFloat(string s, out float value)
			=> float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

		static readonly Dictionary<Key, KeyCode> s_FieldKeys = new Dictionary<Key, KeyCode>
		{
			[Key.Backspace] = KeyCode.Backspace,
			[Key.Delete] = KeyCode.Delete,
			[Key.Enter] = KeyCode.Return,
			[Key.NumpadEnter] = KeyCode.KeypadEnter,
			[Key.Tab] = KeyCode.Tab,
			[Key.Escape] = KeyCode.Escape,
			[Key.LeftArrow] = KeyCode.LeftArrow,
			[Key.RightArrow] = KeyCode.RightArrow,
			[Key.UpArrow] = KeyCode.UpArrow,
			[Key.DownArrow] = KeyCode.DownArrow,
			[Key.Home] = KeyCode.Home,
			[Key.End] = KeyCode.End,
			[Key.PageUp] = KeyCode.PageUp,
			[Key.PageDown] = KeyCode.PageDown,
		};

		/// <summary>
		/// The IMGUI KeyDown an input field would have received for this combo:
		/// one editing/navigation key, or a letter with Ctrl/Cmd (select all, copy, ...).
		/// </summary>
		static bool TryFieldEvent(List<Key> keys, out Event evt)
		{
			evt = null;
			var modifiers = EventModifiers.None;
			var others = new List<Key>();
			foreach (var key in keys)
			{
				switch (key)
				{
					case Key.LeftShift: case Key.RightShift: modifiers |= EventModifiers.Shift; break;
					case Key.LeftCtrl: case Key.RightCtrl: modifiers |= EventModifiers.Control; break;
					case Key.LeftAlt: case Key.RightAlt: modifiers |= EventModifiers.Alt; break;
					case Key.LeftMeta: case Key.RightMeta: modifiers |= EventModifiers.Command; break;
					default: others.Add(key); break;
				}
			}

			KeyCode? keyCode = null;
			foreach (var key in others)
			{
				if (s_FieldKeys.TryGetValue(key, out var mapped))
					keyCode = mapped;
				else if (key >= Key.A && key <= Key.Z && (modifiers & (EventModifiers.Control | EventModifiers.Command)) != 0)
					keyCode = KeyCode.A + (key - Key.A);
				else
					return false;
			}
			if (keyCode == null)
				return false;
			evt = new Event { type = EventType.KeyDown, keyCode = keyCode.Value, modifiers = modifiers };
			return true;
		}
	}
}
