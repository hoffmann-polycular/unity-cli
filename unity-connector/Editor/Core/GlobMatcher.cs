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



using System.Text;
using System.Text.RegularExpressions;

namespace UnityCliConnector
{
	/// <summary>
	/// Compiles shell-style globs (<c>*</c>, <c>?</c>) into anchored regexes.
	/// A literal-with-no-wildcards glob still works — it becomes an exact match.
	/// </summary>
	public static class GlobMatcher
	{
		public static Regex Compile(string glob)
		{
			if (glob == null) return null;
			var sb = new StringBuilder("^");
			foreach (var c in glob)
			{
				switch (c)
				{
					case '*': sb.Append(".*"); break;
					case '?': sb.Append('.'); break;
					case '.':
					case '\\':
					case '+':
					case '(':
					case ')':
					case '[':
					case ']':
					case '{':
					case '}':
					case '^':
					case '$':
					case '|':
						sb.Append('\\').Append(c);
						break;
					default:
						sb.Append(c);
						break;
				}
			}
			sb.Append('$');
			return new Regex(sb.ToString(), RegexOptions.CultureInvariant);
		}
	}
}
