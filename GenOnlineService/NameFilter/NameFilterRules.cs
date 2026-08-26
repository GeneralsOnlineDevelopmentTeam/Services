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

using Sentry;

namespace GenOnlineService.NameFilter
{
	// A pattern plus what to do when a name matches it, loaded from data/namefilter_rules.txt.
	public class NameFilterRule
	{
		// the line this rule is on in the rules file, so a report points at the line to edit
		public int ID { get; set; }

		// written in the file as typed, folded for its match type by NameFilterService.Compile
		public string Pattern { get; set; } = String.Empty;

		public ENameRuleMatch MatchType { get; set; } = ENameRuleMatch.Skeleton;
		public ENameRuleAction Action { get; set; } = ENameRuleAction.Block;

		public string Category { get; set; } = String.Empty;
	}

	public static class NameFilterRules
	{
		// Null means unreadable, which the caller treats differently from an empty file: an empty
		// file is somebody clearing the rules on purpose.
		public static List<NameFilterRule>? Load()
		{
			string strPath = Path.Combine("data", "namefilter_rules.txt");

			try
			{
				if (!System.IO.File.Exists(strPath))
				{
					Console.WriteLine($"[NAMEFILTER] {strPath} does not exist, no rules loaded");
					return new List<NameFilterRule>();
				}

				string[] strLines = System.IO.File.ReadAllLines(strPath);

				List<NameFilterRule> lstRules = new();

				for (int lineNumber = 1; lineNumber <= strLines.Length; ++lineNumber)
				{
					string strLine = strLines[lineNumber - 1].Trim();
					if (strLine.Length == 0 || strLine[0] == '#')
					{
						continue;
					}

					// no field may contain whitespace, so a hand-typed space separates as well
					string[] strFields = strLine.Split(new[] { '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries);
					if (strFields.Length < 3)
					{
						Console.WriteLine($"[NAMEFILTER] {strPath}:{lineNumber} needs at least action, match and pattern, ignored");
						continue;
					}

					// TryParse accepts any number, so IsDefined is what rejects a typo
					if (!Enum.TryParse(strFields[0], true, out ENameRuleAction action) || !Enum.IsDefined(action))
					{
						Console.WriteLine($"[NAMEFILTER] {strPath}:{lineNumber} '{strFields[0]}' is not allow, block, review or shadow, ignored");
						continue;
					}

					if (!Enum.TryParse(strFields[1], true, out ENameRuleMatch matchType) || !Enum.IsDefined(matchType))
					{
						Console.WriteLine($"[NAMEFILTER] {strPath}:{lineNumber} '{strFields[1]}' is not skeleton, word or exact, ignored");
						continue;
					}

					if (strFields[2].Length > 64)
					{
						Console.WriteLine($"[NAMEFILTER] {strPath}:{lineNumber} pattern is longer than 64 characters, ignored");
						continue;
					}

					lstRules.Add(new NameFilterRule
					{
						ID = lineNumber,
						Pattern = strFields[2],
						MatchType = matchType,
						Action = action,
						Category = strFields.Length > 3 ? strFields[3] : "manual"
					});
				}

				return lstRules;
			}
			catch (Exception ex)
			{
				Console.WriteLine($"[ERROR] NameFilterRules.Load failed: {ex.Message}");
				SentrySdk.CaptureException(ex);
				return null;
			}
		}
	}
}
