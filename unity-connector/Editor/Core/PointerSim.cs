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
using System.Globalization;
using System.Linq;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace UnityCliConnector
{
	/// <summary>
	/// Shared core for the <c>click</c> and <c>drag</c> tools: real pointer
	/// input driven through Unity's <see cref="EventSystem"/> so game logic
	/// decides what is allowed.
	///
	/// Why the EventSystem and not <c>UnityEngine.Input</c>: legacy
	/// <c>Input</c> (mousePosition / GetMouseButtonDown / inputString) is
	/// read-only and cannot be injected, so gameplay that *polls* it directly
	/// cannot be driven under the legacy input backend. What CAN be driven is
	/// the EventSystem layer — <see cref="EventSystem.RaycastAll"/> plus
	/// <see cref="ExecuteEvents"/> — which respects every registered raycaster
	/// (GraphicRaycaster for uGUI, PhysicsRaycaster / Physics2DRaycaster for
	/// 3D/2D), interactable/CanvasGroup gating, and occlusion, and fires the
	/// genuine IPointerClickHandler / IBeginDragHandler / IDropHandler
	/// callbacks. When a target is not reachable through that layer we fail
	/// clearly (diagnosing why) rather than dispatching to it directly, so QA
	/// never reaches a state a player could not.
	///
	/// Structured so a future new-Input-System device-injection backend can
	/// reuse the screen-point geometry and swap only the "emit events" step.
	/// </summary>
	public static class PointerSim
	{
		/// <summary>A resolved click/drag target: a screen point plus, for
		/// element (path) locations, the GameObject it was resolved from
		/// (used for raycast gating). Raw-coordinate locations have a null
		/// <see cref="Element"/> and are dispatched to whatever is under the
		/// point.</summary>
		public struct Location
		{
			public Vector2 Point;      // Unity screen space, bottom-left origin, pixels
			public GameObject Element; // null for a raw coordinate
			public string Label;
		}

		// ── preconditions ───────────────────────────────────────────────────

		public static Result<bool> EnsureReady()
		{
			if (!Application.isPlaying)
				return Result<bool>.Error(
					"Input simulation requires play mode — run 'unity-cli editor play' first. " +
					"The EventSystem only routes pointer events while the game is running.",
					ErrorKind.Runtime);
			if (EventSystem.current == null)
				return Result<bool>.Error(
					"No active EventSystem in the scene — pointer input cannot be routed. " +
					"Add an EventSystem (GameObject > UI > Event System).",
					ErrorKind.Runtime);
			return Result<bool>.Success(true);
		}

		// ── location resolution ─────────────────────────────────────────────

		/// <summary>
		/// Resolve a location token to a screen point. A token that is exactly
		/// two comma-separated numbers is a screen coordinate; anything else is
		/// an element path resolved through <see cref="PathParser"/> /
		/// <see cref="PathResolver"/> — the same content-based disambiguation
		/// `set`/`invoke` use for value args. Coordinates are Unity screen
		/// space (bottom-left origin); <paramref name="flip"/> accepts a
		/// top-left (screenshot) origin and converts; <paramref name="normalized"/>
		/// reads the pair as 0..1 fractions of the Game-View size.
		/// </summary>
		public static Result<Location> ResolveLocation(string token, bool normalized, bool flip)
		{
			if (string.IsNullOrWhiteSpace(token))
				return Result<Location>.Error("empty location.", ErrorKind.Usage);

			if (TryParseCoord(token, out var x, out var y))
			{
				var size = ScreenSize();
				float px = normalized ? x * size.x : x;
				float py = normalized ? y * size.y : y;
				if (flip) py = size.y - py; // top-left (screenshot) → bottom-left (Unity)
				return Result<Location>.Success(new Location
				{
					Point = new Vector2(px, py),
					Element = null,
					Label = $"({px:0.#}, {py:0.#})",
				});
			}

			var parsed = PathParser.Parse(token);
			if (!parsed.IsSuccess)
				return Result<Location>.Error(parsed.ErrorMessage, parsed.ErrorKind);
			var goRes = PathResolver.ResolveGameObject(parsed.Value);
			if (!goRes.IsSuccess)
				return Result<Location>.Error(goRes.ErrorMessage, goRes.ErrorKind);
			var go = goRes.Value;

			var ptRes = ScreenPointForElement(go);
			if (!ptRes.IsSuccess)
				return Result<Location>.Error(ptRes.ErrorMessage, ptRes.ErrorKind);

			return Result<Location>.Success(new Location
			{
				Point = ptRes.Value,
				Element = go,
				Label = PathResolver.GetCanonicalPath(go),
			});
		}

		// Strict "X,Y" — exactly two comma-separated numbers. Anything with a
		// slash, '#', ':' etc. is a path and never matches here. Use './X,Y' to
		// force path interpretation for the pathological name.
		private static bool TryParseCoord(string token, out float x, out float y)
		{
			x = y = 0f;
			var parts = token.Split(',');
			if (parts.Length != 2) return false;
			return float.TryParse(parts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out x)
				&& float.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out y);
		}

		private static Vector2 ScreenSize()
		{
			// In play mode Screen.width/height reflect the Game-View render
			// size — the same pixel space the raycasters operate in.
			var w = Screen.width > 0 ? Screen.width : 1920;
			var h = Screen.height > 0 ? Screen.height : 1080;
			return new Vector2(w, h);
		}

		private static Result<Vector2> ScreenPointForElement(GameObject go)
		{
			// uGUI element: project the rect centre through the canvas camera.
			var rt = go.transform as RectTransform;
			var canvas = go.GetComponentInParent<Canvas>();
			if (rt != null && canvas != null)
			{
				var cam = canvas.renderMode == RenderMode.ScreenSpaceOverlay
					? null
					: (canvas.worldCamera != null ? canvas.worldCamera : Camera.main);
				var worldCentre = rt.TransformPoint(rt.rect.center);
				var sp = RectTransformUtility.WorldToScreenPoint(cam, worldCentre);
				return Result<Vector2>.Success(sp);
			}

			// 3D / 2D object: project the bounds centre through the rendering
			// camera. Fall back to the transform position when it has no
			// renderer/collider bounds.
			var cam3d = Camera.main ?? Camera.allCameras.FirstOrDefault(c => c.isActiveAndEnabled);
			if (cam3d == null)
				return Result<Vector2>.Error(
					$"'{PathResolver.GetCanonicalPath(go)}' is not a uGUI element and there is no active " +
					"Camera to project it onto — cannot compute a screen point.",
					ErrorKind.Runtime);

			var worldPoint = go.transform.position;
			var rend = go.GetComponent<Renderer>();
			if (rend != null) worldPoint = rend.bounds.center;
			else
			{
				var col = go.GetComponent<Collider>();
				if (col != null) worldPoint = col.bounds.center;
				else
				{
					var col2d = go.GetComponent<Collider2D>();
					if (col2d != null) worldPoint = col2d.bounds.center;
				}
			}

			var screen = cam3d.WorldToScreenPoint(worldPoint);
			if (screen.z < 0f)
				return Result<Vector2>.Error(
					$"'{PathResolver.GetCanonicalPath(go)}' is behind the camera '{cam3d.name}' — " +
					"nothing to click there.",
					ErrorKind.Runtime);
			return Result<Vector2>.Success(new Vector2(screen.x, screen.y));
		}

		// ── raycast + gating ────────────────────────────────────────────────

		private static List<RaycastResult> RaycastAt(Vector2 point, out PointerEventData ped)
		{
			ped = new PointerEventData(EventSystem.current) { position = point };
			var results = new List<RaycastResult>();
			EventSystem.current.RaycastAll(ped, results);
			return results;
		}

		// Would a click at the location's point actually reach its intended
		// element? True when the top hit is the element, a descendant of it, or
		// resolves to the same IPointerClickHandler. Coordinate locations
		// (Element == null) are never gated — they hit whatever is there.
		private static bool Reaches(GameObject hit, GameObject element)
		{
			if (element == null || hit == null) return element == null;
			if (hit == element) return true;
			if (hit.transform.IsChildOf(element.transform)) return true;
			var eh = ExecuteEvents.GetEventHandler<IPointerClickHandler>(element);
			var hh = ExecuteEvents.GetEventHandler<IPointerClickHandler>(hit);
			return eh != null && eh == hh;
		}

		private static string DiagnoseUnreachable(Location loc, List<RaycastResult> results)
		{
			if (results.Count == 0)
			{
				var extra = loc.Element != null && !(loc.Element.transform is RectTransform)
					&& !AnyPhysicsRaycasterPresent()
					? " No PhysicsRaycaster is present on any camera, so 3D/2D colliders do not " +
					  "participate in the EventSystem — add one, or the object is driven by direct " +
					  "Input polling (undrivable under the legacy input backend)."
					: "";
				return $"Nothing raycastable at {loc.Label}.{extra}";
			}
			var top = results[0].gameObject;
			return $"'{loc.Label}' is not reachable at its screen point — the top hit there is " +
				$"'{PathResolver.GetCanonicalPath(top)}' (occluded, or a different object). " +
				"No bypass: a player could not click it either.";
		}

		private static bool AnyPhysicsRaycasterPresent()
		{
			return Object.FindObjectsOfType<BaseRaycaster>()
				.Any(r => r is PhysicsRaycaster || r is Physics2DRaycaster);
		}

		// A Selectable that is non-interactable (directly or via a CanvasGroup)
		// is refused, mirroring the player experience.
		private static Selectable NonInteractable(GameObject go)
		{
			if (go == null) return null;
			var sel = go.GetComponentInParent<Selectable>();
			return (sel != null && !sel.IsInteractable()) ? sel : null;
		}

		// ── click ───────────────────────────────────────────────────────────

		public static object Click(Location loc, PointerEventData.InputButton button, string format)
		{
			var ready = EnsureReady();
			if (!ready.IsSuccess) return ErrorResponse.FromResult(ready);

			var results = RaycastAt(loc.Point, out var ped);
			ped.button = button;

			if (results.Count == 0 || !Reaches(results[0].gameObject, loc.Element))
				return new ErrorResponse(DiagnoseUnreachable(loc, results), ErrorKind.Runtime);

			var top = results[0];
			var go = top.gameObject;

			var blocked = NonInteractable(go);
			if (blocked != null)
				return new ErrorResponse(
					$"'{PathResolver.GetCanonicalPath(blocked.gameObject)}' is a non-interactable " +
					$"{blocked.GetType().Name} — click refused (a player could not click it either).",
					ErrorKind.Runtime);

			ped.pointerCurrentRaycast = top;
			ped.pointerPressRaycast = top;
			ped.pressPosition = loc.Point;

			// Enter → Down → (Select) → Up → Click, mirroring StandaloneInputModule.
			ExecuteEvents.ExecuteHierarchy(go, ped, ExecuteEvents.pointerEnterHandler);

			var pressHandler = ExecuteEvents.ExecuteHierarchy(go, ped, ExecuteEvents.pointerDownHandler);
			if (pressHandler == null)
				pressHandler = ExecuteEvents.GetEventHandler<IPointerClickHandler>(go);
			ped.pointerPress = pressHandler;
			ped.rawPointerPress = go;
			ped.eligibleForClick = true;

			var selectHandler = ExecuteEvents.GetEventHandler<ISelectHandler>(go);
			if (selectHandler != null)
				EventSystem.current.SetSelectedGameObject(selectHandler, ped);

			ExecuteEvents.Execute(pressHandler, ped, ExecuteEvents.pointerUpHandler);

			var clickHandler = ExecuteEvents.GetEventHandler<IPointerClickHandler>(go);
			ExecuteEvents.Execute(clickHandler, ped, ExecuteEvents.pointerClickHandler);

			ExecuteEvents.ExecuteHierarchy(go, ped, ExecuteEvents.pointerExitHandler);

			var handlerPath = clickHandler != null ? PathResolver.GetCanonicalPath(clickHandler) : null;
			var summary = handlerPath != null
				? $"clicked {loc.Label} → {handlerPath} ({button})"
				: $"clicked {loc.Label} — no IPointerClickHandler handled it ({button})";

			return Render(summary, format, new Dictionary<string, object>
			{
				["clicked"] = loc.Label,
				["at"] = new[] { loc.Point.x, loc.Point.y },
				["button"] = button.ToString(),
				["hit"] = PathResolver.GetCanonicalPath(go),
				["handler"] = handlerPath,
			});
		}

		// ── drag ────────────────────────────────────────────────────────────

		public static object Drag(Location from, Location to, int steps, string format)
		{
			var ready = EnsureReady();
			if (!ready.IsSuccess) return ErrorResponse.FromResult(ready);
			if (steps < 1) steps = 1;

			var fromResults = RaycastAt(from.Point, out var ped);
			ped.button = PointerEventData.InputButton.Left;

			if (fromResults.Count == 0 || !Reaches(fromResults[0].gameObject, from.Element))
				return new ErrorResponse(DiagnoseUnreachable(from, fromResults), ErrorKind.Runtime);

			var topFrom = fromResults[0];
			var go = topFrom.gameObject;

			ped.pointerCurrentRaycast = topFrom;
			ped.pointerPressRaycast = topFrom;
			ped.pressPosition = from.Point;

			ExecuteEvents.ExecuteHierarchy(go, ped, ExecuteEvents.pointerEnterHandler);
			var pressHandler = ExecuteEvents.ExecuteHierarchy(go, ped, ExecuteEvents.pointerDownHandler);
			if (pressHandler == null)
				pressHandler = ExecuteEvents.GetEventHandler<IPointerClickHandler>(go);
			ped.pointerPress = pressHandler;
			ped.rawPointerPress = go;

			var dragHandler = ExecuteEvents.GetEventHandler<IDragHandler>(go);
			if (dragHandler == null)
			{
				// Release the press we started so we don't leave a dangling
				// pointer-down, then report clearly.
				ExecuteEvents.Execute(pressHandler, ped, ExecuteEvents.pointerUpHandler);
				return new ErrorResponse(
					$"Nothing at '{from.Label}' handles dragging (no IDragHandler / IBeginDragHandler " +
					"in its hierarchy). Under legacy input, drags implemented via direct Input polling " +
					"are not drivable.",
					ErrorKind.Runtime);
			}
			ped.pointerDrag = dragHandler;

			ExecuteEvents.ExecuteHierarchy(go, ped, ExecuteEvents.initializePotentialDrag);
			ExecuteEvents.Execute(dragHandler, ped, ExecuteEvents.beginDragHandler);
			ped.dragging = true;

			var prev = from.Point;
			for (var i = 1; i <= steps; i++)
			{
				var cur = Vector2.Lerp(from.Point, to.Point, (float)i / steps);
				ped.position = cur;
				ped.delta = cur - prev;
				prev = cur;
				var stepResults = new List<RaycastResult>();
				EventSystem.current.RaycastAll(ped, stepResults);
				ped.pointerCurrentRaycast = stepResults.Count > 0 ? stepResults[0] : default;
				ExecuteEvents.Execute(dragHandler, ped, ExecuteEvents.dragHandler);
			}

			// Release over the drop point: whatever is under `to` receives the
			// drop (drop targets are not gated — you release over a point).
			ped.position = to.Point;
			ped.delta = to.Point - prev;
			var dropResults = new List<RaycastResult>();
			EventSystem.current.RaycastAll(ped, dropResults);
			var dropTop = dropResults.Count > 0 ? dropResults[0] : default;
			ped.pointerCurrentRaycast = dropTop;

			ExecuteEvents.Execute(dragHandler, ped, ExecuteEvents.endDragHandler);
			ped.dragging = false;

			GameObject dropTarget = null;
			if (dropTop.gameObject != null)
			{
				var dropHandled = ExecuteEvents.ExecuteHierarchy(dropTop.gameObject, ped, ExecuteEvents.dropHandler);
				if (dropHandled != null) dropTarget = dropHandled;
			}

			ped.eligibleForClick = false;
			ExecuteEvents.Execute(pressHandler, ped, ExecuteEvents.pointerUpHandler);
			ExecuteEvents.ExecuteHierarchy(go, ped, ExecuteEvents.pointerExitHandler);

			var draggedPath = PathResolver.GetCanonicalPath(
				(dragHandler.transform ? dragHandler.transform.gameObject : go));
			var dropPath = dropTarget != null ? PathResolver.GetCanonicalPath(dropTarget) : null;
			var summary = dropPath != null
				? $"dragged {from.Label} → {to.Label}; dropped on {dropPath}"
				: $"dragged {from.Label} → {to.Label} (no IDropHandler at the release point)";

			return Render(summary, format, new Dictionary<string, object>
			{
				["from"] = from.Label,
				["to"] = to.Label,
				["dragged"] = draggedPath,
				["dropTarget"] = dropPath,
				["steps"] = steps,
			});
		}

		// ── rendering ───────────────────────────────────────────────────────

		private static object Render(string summary, string format, Dictionary<string, object> data)
		{
			if (format == "json")
				return new SuccessResponse(summary, data);
			// Plain: emit the one-line summary as the payload so it prints raw.
			return new SuccessResponse(summary, summary);
		}
	}
}
