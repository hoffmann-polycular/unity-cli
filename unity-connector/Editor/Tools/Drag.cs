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

namespace UnityCliConnector.Tools
{
	/// <summary>
	/// Synthesises a real pointer drag (down → move → up + drop) through
	/// Unity's EventSystem so drag-and-drop game logic runs exactly as for a
	/// player (see <see cref="PointerSim"/>). Requires play mode. Both endpoints
	/// are locations — an element path or a screen coordinate 'X,Y'. The press
	/// endpoint is raycast-gated (must land on the intended draggable); the drop
	/// endpoint dispatches to whatever IDropHandler is under the release point.
	/// </summary>
	[UnityCliTool(Name = "drag", Group = "Editor",
		Description = "Drag from one UI element/coordinate to another via the real EventSystem (play mode).")]
	public static class Drag
	{
		private const int DefaultSteps = 8;

		public class Parameters
		{
			[ToolParameter("Drag start: an element path or a screen coordinate 'X,Y'.", Required = true)]
			public string From { get; set; }

			[ToolParameter("Drag end: an element path or a screen coordinate 'X,Y'.", Required = true)]
			public string To { get; set; }

			[ToolParameter("Interpolation steps between the endpoints (default 8).")]
			public int Steps { get; set; }

			[ToolParameter("Interpret X,Y as 0..1 fractions of the Game-View size instead of pixels.")]
			public bool Normalized { get; set; }

			[ToolParameter("X,Y are top-left origin (e.g. a screenshot pixel); convert to Unity bottom-left space.")]
			public bool Flip { get; set; }
		}

		public static object HandleCommand(JObject @params)
		{
			var p = new ToolParams(@params);
			var args = p.GetRaw("args") as JArray;

			// Endpoints from explicit params or the first two positionals.
			var fromTok = p.Get("from");
			var toTok = p.Get("to");
			if (string.IsNullOrWhiteSpace(fromTok) && args != null && args.Count > 0) fromTok = args[0]?.ToString();
			if (string.IsNullOrWhiteSpace(toTok) && args != null && args.Count > 1) toTok = args[1]?.ToString();

			if (string.IsNullOrWhiteSpace(fromTok) || string.IsNullOrWhiteSpace(toTok))
				return new ErrorResponse(
					"drag requires two locations: <from> <to> (each an element path or a screen coordinate 'X,Y').",
					ErrorKind.Usage);

			var steps = p.GetInt("steps") ?? DefaultSteps;
			var normalized = p.GetBool("normalized");
			var flip = p.GetBool("flip");
			var format = (p.Get("format") ?? "plain").ToLowerInvariant();

			var fromRes = PointerSim.ResolveLocation(fromTok, normalized, flip);
			if (!fromRes.IsSuccess) return ErrorResponse.FromResult(fromRes);
			var toRes = PointerSim.ResolveLocation(toTok, normalized, flip);
			if (!toRes.IsSuccess) return ErrorResponse.FromResult(toRes);

			return PointerSim.Drag(fromRes.Value, toRes.Value, steps, format);
		}
	}
}
