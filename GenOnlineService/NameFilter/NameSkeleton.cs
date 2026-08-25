/*
**    GeneralsOnline Game Services - Backend Services for Command & Conquer Generals Online: Zero Hour
**    Copyright (C) 2025  GeneralsOnline Development Team
**
**    This program is free software: you can redistribute it and/or modify
**    it under the terms of the GNU Affero General Public License as
**    published by the Free Software Foundation, either version 3 of the
**    License, or (at your option) any later version.
**
**    This program is distributed in the hope that it will be useful,
**    but WITHOUT ANY WARRANTY; without even the implied warranty of
**    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
**    GNU Affero General Public License for more details.
**
**    You should have received a copy of the GNU Affero General Public License
**    along with this program.  If not, see <https://www.gnu.org/licenses/>.
*/

using System.Globalization;
using System.Text;

namespace GenOnlineService.NameFilter
{
	// Canonical forms of a display name. Rule patterns and candidate names both go through this,
	// so both sides land in the same alphabet.
	public static class NameSkeleton
	{
		// A confusable can expand to several ASCII characters, so a name inside the 16 character
		// limit can still fold to more than this.
		public const int MaxSkeletonLength = 32;

		// Unicode confusable -> ASCII, from UTS #39. ASCII sources are not in here, the leet fold
		// covers those. See data/confusables_ascii.tsv for the source and the reduction rule.
		private static Dictionary<int, string> g_dictConfusables = new();
		private static bool g_bConfusablesLoaded = false;

		// Applied after the confusable fold, to rules and names alike. Lossy on purpose.
		private static readonly Dictionary<char, char> g_dictLeet = new()
		{
			{ '1', 'i' }, { 'l', 'i' }, { '|', 'i' }, { '!', 'i' },
			{ '0', 'o' },
			{ '3', 'e' },
			{ '4', 'a' }, { '@', 'a' },
			{ '5', 's' }, { '$', 's' },
			{ '7', 't' },
			{ '8', 'b' }
		};

		public static void LoadConfusables()
		{
			if (g_bConfusablesLoaded)
			{
				return;
			}

			g_bConfusablesLoaded = true;

			try
			{
				string strPath = Path.Combine("data", "confusables_ascii.tsv");
				if (!System.IO.File.Exists(strPath))
				{
					Console.WriteLine($"[ERROR] NameSkeleton: {strPath} is missing, homoglyph folding is disabled");
					return;
				}

				Dictionary<int, string> dictMappings = new();

				foreach (string strLine in System.IO.File.ReadLines(strPath))
				{
					if (strLine.Length == 0 || strLine[0] == '#')
					{
						continue;
					}

					string[] strParts = strLine.Split('\t');
					if (strParts.Length != 2)
					{
						continue;
					}

					if (Int32.TryParse(strParts[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int codepoint))
					{
						dictMappings[codepoint] = strParts[1];
					}
				}

				g_dictConfusables = dictMappings;
			}
			catch (Exception ex)
			{
				Console.WriteLine($"[ERROR] NameSkeleton.LoadConfusables failed: {ex.Message}");
				SentrySdk.CaptureException(ex);
			}
		}

		public static int GetNumConfusables()
		{
			return g_dictConfusables.Count;
		}

		// NFKD, combining marks dropped, confusables folded to ASCII, lowercased. Separators are
		// kept - word matching needs them.
		public static string Normalize(string strName)
		{
			if (String.IsNullOrEmpty(strName))
			{
				return String.Empty;
			}

			string strDecomposed;
			try
			{
				strDecomposed = strName.Normalize(NormalizationForm.FormKD);
			}
			catch (ArgumentException)
			{
				// unpaired surrogates and other malformed input
				strDecomposed = strName;
			}

			StringBuilder builder = new StringBuilder(strDecomposed.Length);

			for (int i = 0; i < strDecomposed.Length; ++i)
			{
				bool bSurrogatePair = Char.IsSurrogatePair(strDecomposed, i);
				if (!bSurrogatePair && Char.IsSurrogate(strDecomposed[i]))
				{
					// unpaired surrogate, not a character
					continue;
				}

				UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(strDecomposed, i);
				int codepoint = Char.ConvertToUtf32(strDecomposed, i);

				if (bSurrogatePair)
				{
					++i;
				}

				if (IsInvisible(codepoint) || category == UnicodeCategory.NonSpacingMark)
				{
					continue;
				}

				if (g_dictConfusables.TryGetValue(codepoint, out string? strMapped))
				{
					builder.Append(strMapped);
				}
				else
				{
					builder.Append(Char.ConvertFromUtf32(codepoint));
				}
			}

			return builder.ToString().ToLowerInvariant().Trim();
		}

		// Normalized form plus the leet fold, repeat runs collapsed, everything outside [a-z0-9]
		// removed. This is what substring rules match against.
		public static string Skeletonize(string strName)
		{
			string strNormalized = Normalize(strName);

			StringBuilder builder = new StringBuilder(strNormalized.Length);
			char lastAppended = '\0';

			foreach (char c in strNormalized)
			{
				char folded = g_dictLeet.TryGetValue(c, out char mapped) ? mapped : c;

				if (!((folded >= 'a' && folded <= 'z') || (folded >= '0' && folded <= '9')))
				{
					continue;
				}

				if (folded == lastAppended)
				{
					continue;
				}

				builder.Append(folded);
				lastAppended = folded;

				if (builder.Length == MaxSkeletonLength)
				{
					break;
				}
			}

			return builder.ToString();
		}

		public static bool IsInvisible(int codepoint)
		{
			// zero width space/non-joiner/joiner, LRM/RLM, word joiner, BOM
			if (codepoint >= 0x200B && codepoint <= 0x200F)
			{
				return true;
			}

			// bidi embedding/override
			if (codepoint >= 0x202A && codepoint <= 0x202E)
			{
				return true;
			}

			// bidi isolates
			if (codepoint >= 0x2066 && codepoint <= 0x2069)
			{
				return true;
			}

			if (codepoint == 0x2060 || codepoint == 0xFEFF || codepoint == 0x00AD || codepoint == 0x180E)
			{
				return true;
			}

			// variation selectors
			if (codepoint >= 0xFE00 && codepoint <= 0xFE0F)
			{
				return true;
			}

			// tag characters
			if (codepoint >= 0xE0000 && codepoint <= 0xE007F)
			{
				return true;
			}

			return false;
		}
	}
}
