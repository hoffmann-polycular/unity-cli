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
using UnityEngine.EventSystems;

namespace UnityCliConnector.Tools
{
	/// <summary>
	/// Synthesises a real pointer click through Unity's EventSystem so game
	/// logic decides what is allowed (see <see cref="PointerSim"/>). Requires
	/// play mode. The target is a single location — an element path (raycast at
	/// its screen centre) or a screen coordinate <c>X,Y</c> — disambiguated by
	/// content the same way `set`/`invoke` treat value args.
	/// </summary>
	[UnityCliTool(Name = "click", Group = "Editor",
		Description = "Click a UI element or screen coordinate via the real EventSystem (play mode). Respects raycast/interactable gating.")]
	public static class Click
	{
		public class Parameters
		{
			[ToolParameter("Location: an element path (e.g. /World/UI/Button) or a screen coordinate 'X,Y'.", Required = true)]
			public string Location { get; set; }

			[ToolParameter("Mouse button: left (default), right, or middle.")]
			public string Button { get; set; }

			[ToolParameter("Interpret X,Y as 0..1 fractions of the Game-View size instead of pixels.")]
			public bool Normalized { get; set; }

			[ToolParameter("X,Y are top-left origin (e.g. a screenshot pixel); convert to Unity bottom-left space.")]
			public bool Flip { get; set; }
		}

		public static object HandleCommand(JObject @params)
		{
			var p = new ToolParams(@params);
			var args = p.GetRaw("args") as JArray;
			var location = p.Get("location")
				?? (args != null && args.Count > 0 ? args[0]?.ToString() : null);

			if (string.IsNullOrWhiteSpace(location))
				return new ErrorResponse(
					"click requires a location — an element path or a screen coordinate 'X,Y'.",
					ErrorKind.Usage);

			var btnRes = ParseButton(p.Get("button"));
			if (!btnRes.IsSuccess) return ErrorResponse.FromResult(btnRes);

			var normalized = p.GetBool("normalized");
			var flip = p.GetBool("flip");
			var format = (p.Get("format") ?? "plain").ToLowerInvariant();

			var locRes = PointerSim.ResolveLocation(location, normalized, flip);
			if (!locRes.IsSuccess) return ErrorResponse.FromResult(locRes);

			return PointerSim.Click(locRes.Value, btnRes.Value, format);
		}

		private static Result<PointerEventData.InputButton> ParseButton(string s)
		{
			if (string.IsNullOrWhiteSpace(s))
				return Result<PointerEventData.InputButton>.Success(PointerEventData.InputButton.Left);
			switch (s.Trim().ToLowerInvariant())
			{
				case "left": return Result<PointerEventData.InputButton>.Success(PointerEventData.InputButton.Left);
				case "right": return Result<PointerEventData.InputButton>.Success(PointerEventData.InputButton.Right);
				case "middle": return Result<PointerEventData.InputButton>.Success(PointerEventData.InputButton.Middle);
				default:
					return Result<PointerEventData.InputButton>.Error(
						$"invalid --button '{s}' (expected left, right, or middle).", ErrorKind.Usage);
			}
		}
	}
}
