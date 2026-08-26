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

using Microsoft.EntityFrameworkCore;
using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;

namespace GenOnlineService.NameFilter
{
	public enum ENameRuleAction
	{
		Allow = 0,      // allowlist, wins over everything else
		Block = 1,      // hard reject
		Review = 2,     // accepted, announced to the admin channel
		Shadow = 3      // accepted, logged only (used to trial a rule)
	}

	public enum ENameRuleMatch
	{
		Skeleton = 0,   // substring of the skeleton
		Word = 1,       // word boundaries in the normalized text
		Exact = 2       // whole skeleton
	}

	public enum ENameRejectSource
	{
		NameChange = 0, // someone tried to set this name
		Rescan = 1      // a name already in the database, found by !namefilter rescan
	}

	public enum ENameCheckResult
	{
		Accepted = 0,
		TooShort,
		TooLong,
		InvalidCharacters,
		EmptyName,
		SurroundingWhitespace,
		Blocked,
		NameTaken,
		RateLimited
	}

	public class NameCheck
	{
		public ENameCheckResult Result = ENameCheckResult.Accepted;
		public string Normalized = String.Empty;
		public string Skeleton = String.Empty;

		public NameFilterRule? MatchedRule = null;

		// only set for RateLimited
		public int SecondsRemaining = 0;

		public bool IsAccepted()
		{
			return Result == ENameCheckResult.Accepted;
		}
	}

	// How much a hit is worth a human's attention. Derived from the rule that fired rather than
	// stored, so retuning a rule retunes its hits with it.
	public enum ENameSeverity
	{
		Low = 0,        // shadow rules and structural failures - nothing was going to be blocked
		Medium = 1,     // review rules, and word matches, which are the deliberately cautious ones
		High = 2        // a block rule matched
	}

	public class NameScanRuleGroup
	{
		public int RuleID = -1;
		public string Pattern = String.Empty;
		public ENameRuleMatch MatchType = ENameRuleMatch.Skeleton;
		public ENameRuleAction Action = ENameRuleAction.Block;

		public int NumHits = 0;
		public int NumSkeletons = 0;
	}

	public class NameScanSkeletonGroup
	{
		public string Skeleton { get; set; } = String.Empty;
		public int NumUsers { get; set; } = 0;
		public string SampleName { get; set; } = String.Empty;
	}

	public class NameDecisionResult
	{
		public int NumRows = 0;

		// allow lines a verdict of keep asks for, reported for an admin to paste into the rules file
		public List<string> AllowLines = new();
		public int NumRenamed = 0;
		public int NumUnsure = 0;
		public int NumSkipped = 0;
		public int NumFailed = 0;

		public string Error = String.Empty;
	}

	public class NameScanResult
	{
		public int NumScanned = 0;
		public int NumHits = 0;
		public int NumRenamed = 0;

		public List<string> Samples = new();
	}

	public class NameFilterService
	{
		private const int MinNameLength = 3;
		private const int MaxNameLength = 16;

		private const int AcceptedChangeCooldownSeconds = 600;
		private const int RejectWindowSeconds = 600;
		private const int MaxRejectsPerWindow = 5;

		private const int BackfillBatchSize = 2000;
		private const int RateLimitPruneThreshold = 4096;

		private const int ScanBatchSize = 1000;
		private const int ScanSampleCount = 20;
		private const int ReplacementNameAttempts = 8;

		private class CompiledRule
		{
			public NameFilterRule Rule = new();
			public string Pattern = String.Empty;
			public Regex? WordRegex = null;
		}

		private readonly IDbContextFactory<AppDbContext> m_dbFactory;

		private volatile List<CompiledRule> m_lstRules = new();

		private readonly ConcurrentDictionary<Int64, DateTime> m_dictLastAcceptedChange = new();
		private readonly ConcurrentDictionary<Int64, List<DateTime>> m_dictRecentRejects = new();

		public NameFilterService(IDbContextFactory<AppDbContext> dbFactory)
		{
			m_dbFactory = dbFactory;
		}

		public void Initialize()
		{
			NameSkeleton.LoadConfusables();

			ReloadRules();

			Console.WriteLine($"[NAMEFILTER] {NameSkeleton.GetNumConfusables()} confusables, {m_lstRules.Count} rules");

			// six figures of rows, too slow to hold up startup, so it runs behind it
			_ = Task.Run(BackfillSkeletons);
		}

		private async Task BackfillSkeletons()
		{
			try
			{
				int numTotal = 0;

				while (true)
				{
					await using var db = await m_dbFactory.CreateDbContextAsync();

					int numBackfilled = await global::Database.NameFilter.BackfillSkeletons(db, BackfillBatchSize);
					if (numBackfilled == 0)
					{
						break;
					}

					numTotal += numBackfilled;
				}

				if (numTotal > 0)
				{
					Console.WriteLine($"[NAMEFILTER] backfilled {numTotal} display name skeletons");
				}
			}
			catch (Exception ex)
			{
				Console.WriteLine($"[ERROR] NameFilterService.BackfillSkeletons failed: {ex.Message}");
				SentrySdk.CaptureException(ex);
			}
		}

		// Returns the number of rules loaded, or -1 if the file could not be read - keeping the
		// rules already in memory rather than turning the filter off.
		public int ReloadRules()
		{
			List<NameFilterRule>? lstRules = NameFilterRules.Load();
			if (lstRules == null)
			{
				Console.WriteLine($"[ERROR] NameFilterService: could not read the rules, keeping the {m_lstRules.Count} already loaded");
				return -1;
			}

			return ApplyRules(lstRules);
		}

		private int ApplyRules(List<NameFilterRule> lstRules)
		{
			List<CompiledRule> lstCompiled = new();

			foreach (NameFilterRule rule in lstRules)
			{
				CompiledRule? compiled = Compile(rule);
				if (compiled != null)
				{
					lstCompiled.Add(compiled);
				}
			}

			// allowlist first, then hard blocks, so the cheapest decisive answer comes out first
			lstCompiled = lstCompiled.OrderBy(r => (int)r.Rule.Action).ToList();

			m_lstRules = lstCompiled;

			return m_lstRules.Count;
		}

		public int GetNumRules()
		{
			return m_lstRules.Count;
		}

		public List<NameFilterRule> GetRules()
		{
			return m_lstRules.Select(r => r.Rule).ToList();
		}

		// the scan report names the rule behind each group
		public Dictionary<int, NameFilterRule> GetRuleDictionary()
		{
			return m_lstRules.ToDictionary(r => r.Rule.ID, r => r.Rule);
		}

		public List<string> GetCategories()
		{
			return m_lstRules.Select(r => r.Rule.Category).Distinct().OrderBy(c => c).ToList();
		}

		private static CompiledRule? Compile(NameFilterRule rule)
		{
			try
			{
				if (rule.MatchType == ENameRuleMatch.Word)
				{
					string strPattern = NameSkeleton.Normalize(rule.Pattern);
					if (String.IsNullOrEmpty(strPattern))
					{
						return null;
					}

					// Not RegexOptions.Compiled: this is a literal with two lookarounds, and the
					// substring pre-filter in Matches keeps it from running at all in the normal case.
					return new CompiledRule
					{
						Rule = rule,
						Pattern = strPattern,
						WordRegex = new Regex($"(?<![a-z0-9]){Regex.Escape(strPattern)}(?![a-z0-9])", RegexOptions.CultureInvariant)
					};
				}

				string strSkeleton = NameSkeleton.Skeletonize(rule.Pattern);
				if (String.IsNullOrEmpty(strSkeleton))
				{
					return null;
				}

				return new CompiledRule
				{
					Rule = rule,
					Pattern = strSkeleton
				};
			}
			catch (Exception ex)
			{
				Console.WriteLine($"[ERROR] NameFilterService.Compile failed for rule {rule.ID}: {ex.Message}");
				SentrySdk.CaptureException(ex);
				return null;
			}
		}

		// Structural gate plus the rules. In memory only, no database access - safe to call from
		// the admin !namefilter test command as well as from the name change path.
		public NameCheck Check(string strName)
		{
			NameCheck check = new NameCheck();

			if (String.IsNullOrEmpty(strName))
			{
				check.Result = ENameCheckResult.EmptyName;
				return check;
			}

			if (strName != strName.Trim())
			{
				check.Result = ENameCheckResult.SurroundingWhitespace;
				return check;
			}

			if (strName.Length < MinNameLength)
			{
				check.Result = ENameCheckResult.TooShort;
				return check;
			}

			if (strName.Length > MaxNameLength)
			{
				check.Result = ENameCheckResult.TooLong;
				return check;
			}

			foreach (char c in strName)
			{
				if (Char.IsControl(c) || NameSkeleton.IsInvisible(c))
				{
					check.Result = ENameCheckResult.InvalidCharacters;
					return check;
				}

				// anything that is not a plain space but still whitespace is a separator trick
				if (Char.IsWhiteSpace(c) && c != ' ')
				{
					check.Result = ENameCheckResult.InvalidCharacters;
					return check;
				}
			}

			check.Normalized = NameSkeleton.Normalize(strName);
			check.Skeleton = NameSkeleton.Skeletonize(strName);

			// Checked on the normalized form, not the skeleton: the skeleton alphabet is ASCII, so
			// a name in a script that does not fold onto it is empty there and still a good name.
			if (!check.Normalized.Any(Char.IsLetterOrDigit))
			{
				check.Result = ENameCheckResult.EmptyName;
				return check;
			}

			foreach (CompiledRule compiled in m_lstRules)
			{
				if (!Matches(compiled, check))
				{
					continue;
				}

				if (compiled.Rule.Action == ENameRuleAction.Allow)
				{
					return check;
				}

				check.MatchedRule = compiled.Rule;

				if (compiled.Rule.Action == ENameRuleAction.Block)
				{
					check.Result = ENameCheckResult.Blocked;
				}

				// Review and Shadow leave the result Accepted, the caller reports them
				return check;
			}

			return check;
		}

		public static ENameSeverity GetSeverity(NameCheck check)
		{
			if (check.MatchedRule == null)
			{
				// structural failure - odd characters, nothing anyone chose to ban
				return ENameSeverity.Low;
			}

			switch (check.MatchedRule.Action)
			{
				case ENameRuleAction.Block:
					// word rules are the cautious ones, they were written to avoid over-matching
					return check.MatchedRule.MatchType == ENameRuleMatch.Word ? ENameSeverity.Medium : ENameSeverity.High;

				case ENameRuleAction.Review:
					return ENameSeverity.Medium;

				default:
					return ENameSeverity.Low;
			}
		}

		private static bool Matches(CompiledRule compiled, NameCheck check)
		{
			switch (compiled.Rule.MatchType)
			{
				case ENameRuleMatch.Exact:
					return check.Skeleton == compiled.Pattern;

				case ENameRuleMatch.Word:
					// the pattern has to be present at all before its boundaries can matter, and a
					// rescan runs every rule against every name in the database
					return compiled.WordRegex != null
						&& check.Normalized.Contains(compiled.Pattern, StringComparison.Ordinal)
						&& compiled.WordRegex.IsMatch(check.Normalized);

				default:
					return check.Skeleton.Contains(compiled.Pattern, StringComparison.Ordinal);
			}
		}

		// Full name change check: rate limit, rules, skeleton uniqueness. Logs anything that is
		// not a clean accept.
		public async Task<NameCheck> CheckNameChange(AppDbContext db, Int64 userID, string strName, bool bIsAdmin)
		{
			if (!bIsAdmin)
			{
				int secondsRemaining = GetRateLimitRemaining(userID);
				if (secondsRemaining > 0)
				{
					return new NameCheck
					{
						Result = ENameCheckResult.RateLimited,
						SecondsRemaining = secondsRemaining
					};
				}
			}

			NameCheck check = Check(strName);

			// admins are exempt from the rules, not from the structural gate
			if (bIsAdmin && check.Result == ENameCheckResult.Blocked)
			{
				check.Result = ENameCheckResult.Accepted;
			}

			if (check.IsAccepted() && await global::Database.NameFilter.IsSkeletonTaken(db, userID, check.Skeleton))
			{
				check.Result = ENameCheckResult.NameTaken;
			}

			// only bypass hunting counts towards the cooldown - a typo or a taken name should not
			// lock someone out
			if (check.Result == ENameCheckResult.Blocked ||
				check.Result == ENameCheckResult.InvalidCharacters ||
				check.Result == ENameCheckResult.EmptyName)
			{
				RegisterReject(userID);
			}

			if (!check.IsAccepted() || check.MatchedRule != null)
			{
				// The action recorded is what happened, not what the rule says: a rule the admin
				// bypass let through was matched and allowed, which is what Shadow means.
				ENameRuleAction loggedAction = ENameRuleAction.Block;

				if (check.MatchedRule != null)
				{
					loggedAction = check.MatchedRule.Action;

					if (check.IsAccepted() && loggedAction == ENameRuleAction.Block)
					{
						loggedAction = ENameRuleAction.Shadow;
					}
				}

				await global::Database.NameFilter.LogReject(
					db,
					userID,
					strName,
					check.Skeleton,
					check.MatchedRule != null ? check.MatchedRule.ID : -1,
					loggedAction,
					ENameRejectSource.NameChange);
			}

			return check;
		}

		// A Review rule does not block the name, it just makes sure a moderator sees it.
		public void ReportForReview(NameCheck check, Int64 userID, string strName)
		{
			if (check.MatchedRule == null || check.MatchedRule.Action != ENameRuleAction.Review)
			{
				return;
			}

			if (Program.g_Discord == null)
			{
				return;
			}

			Program.g_Discord.PushChannelMessage(EDiscordChannelIDs.AdminCommands,
				$"--NAME FILTER-- user {userID} took the display name `{strName}` (skeleton `{check.Skeleton}`), flagged for review by rule {check.MatchedRule.ID} (`{check.MatchedRule.Pattern}`, {check.MatchedRule.Category}).");
		}

		public void RegisterAcceptedChange(Int64 userID)
		{
			m_dictLastAcceptedChange[userID] = DateTime.UtcNow;
			m_dictRecentRejects.TryRemove(userID, out _);

			PruneRateLimitState();
		}

		// Entries are dead once their window has passed, but nothing walks them, so on a service
		// that stays up for months they would only ever grow.
		private void PruneRateLimitState()
		{
			if (m_dictLastAcceptedChange.Count < RateLimitPruneThreshold && m_dictRecentRejects.Count < RateLimitPruneThreshold)
			{
				return;
			}

			DateTime now = DateTime.UtcNow;

			foreach (var kvPair in m_dictLastAcceptedChange)
			{
				if ((now - kvPair.Value).TotalSeconds >= AcceptedChangeCooldownSeconds)
				{
					m_dictLastAcceptedChange.TryRemove(kvPair.Key, out _);
				}
			}

			foreach (var kvPair in m_dictRecentRejects)
			{
				List<DateTime> lstRejects = kvPair.Value;

				lock (lstRejects)
				{
					lstRejects.RemoveAll(t => (now - t).TotalSeconds >= RejectWindowSeconds);

					if (lstRejects.Count == 0)
					{
						m_dictRecentRejects.TryRemove(kvPair.Key, out _);
					}
				}
			}
		}

		private void RegisterReject(Int64 userID)
		{
			List<DateTime> lstRejects = m_dictRecentRejects.GetOrAdd(userID, _ => new List<DateTime>());

			lock (lstRejects)
			{
				DateTime cutoff = DateTime.UtcNow.AddSeconds(-RejectWindowSeconds);
				lstRejects.RemoveAll(t => t < cutoff);
				lstRejects.Add(DateTime.UtcNow);
			}
		}

		private int GetRateLimitRemaining(Int64 userID)
		{
			DateTime now = DateTime.UtcNow;

			if (m_dictLastAcceptedChange.TryGetValue(userID, out DateTime lastChange))
			{
				double elapsed = (now - lastChange).TotalSeconds;
				if (elapsed < AcceptedChangeCooldownSeconds)
				{
					return (int)Math.Ceiling(AcceptedChangeCooldownSeconds - elapsed);
				}
			}

			if (m_dictRecentRejects.TryGetValue(userID, out List<DateTime>? lstRejects) && lstRejects != null)
			{
				lock (lstRejects)
				{
					DateTime cutoff = now.AddSeconds(-RejectWindowSeconds);
					lstRejects.RemoveAll(t => t < cutoff);

					if (lstRejects.Count >= MaxRejectsPerWindow)
					{
						double elapsed = (now - lstRejects[0]).TotalSeconds;
						return (int)Math.Ceiling(RejectWindowSeconds - elapsed);
					}
				}
			}

			return 0;
		}

		// What the user is told. Deliberately does not name the rule that fired - that goes to
		// name_filter_rejects, where it cannot be used to hunt for a bypass.
		public static string GetUserMessage(NameCheck check, string strRequestedName)
		{
			switch (check.Result)
			{
				case ENameCheckResult.TooShort:
					return String.Format("--NAME CHANGE-- Display names must be at least {0} characters ({1})", MinNameLength, strRequestedName);

				case ENameCheckResult.TooLong:
					return String.Format("--NAME CHANGE-- Display names can be at most {0} characters ({1})", MaxNameLength, strRequestedName);

				case ENameCheckResult.SurroundingWhitespace:
					return String.Format("--NAME CHANGE-- Display names cannot begin or end with spaces ({0})", strRequestedName);

				case ENameCheckResult.InvalidCharacters:
				case ENameCheckResult.EmptyName:
					return String.Format("--NAME CHANGE-- The display name you tried to set contains characters that are not allowed ({0})", strRequestedName);

				case ENameCheckResult.NameTaken:
					return String.Format("--NAME CHANGE-- That display name is too close to one already in use ({0})", strRequestedName);

				case ENameCheckResult.RateLimited:
					return String.Format("--NAME CHANGE-- You are changing your display name too often, try again in {0} seconds", check.SecondsRemaining);

				default:
					return String.Format("--NAME CHANGE-- The display name you tried to set is not allowed ({0})", strRequestedName);
			}
		}

		// Rules only apply at name change time, so names that predate a rule are invisible until this
		// looks at them. With bRename, Block matches are renamed; everything else is reported only.
		public async Task<NameScanResult> ScanExistingNames(bool bRename, int? renameRuleID, string strActor)
		{
			NameScanResult result = new NameScanResult();

			// a scan replaces the previous one, so the report is never a mix of old and current
			await using (var clearDb = await m_dbFactory.CreateDbContextAsync())
			{
				await global::Database.NameFilter.ClearScanHits(clearDb);
			}

			// keyset paged - six figures of users, so never load them all
			Int64 afterUserID = 0;

			while (true)
			{
				await using var db = await m_dbFactory.CreateDbContextAsync();

				List<User> lstUsers = await global::Database.NameFilter.GetUsersWithDisplayName(db, afterUserID, ScanBatchSize);
				if (lstUsers.Count == 0)
				{
					break;
				}

				afterUserID = lstUsers[lstUsers.Count - 1].ID;
				result.NumScanned += lstUsers.Count;

				List<NameFilterReject> lstHits = new();

				foreach (User user in lstUsers)
				{
					string strName = user.DisplayName ?? String.Empty;
					NameCheck check = Check(strName);

					// a clean name. Review and Shadow are accepted but carry a rule, so they stay
					if (check.IsAccepted() && check.MatchedRule == null)
					{
						continue;
					}

					++result.NumHits;

					lstHits.Add(new NameFilterReject
					{
						UserID = user.ID,
						AttemptedName = strName,
						Skeleton = check.Skeleton,
						RuleID = check.MatchedRule != null ? check.MatchedRule.ID : -1,
						Action = check.MatchedRule != null ? check.MatchedRule.Action : ENameRuleAction.Block,
						Source = ENameRejectSource.Rescan,
						Created = DateTime.UtcNow
					});

					bool bRenamed = false;

					// a rename can be scoped to one rule, so a rule set is worked through one decision at a time
					bool bInRenameScope = renameRuleID == null || (check.MatchedRule != null && check.MatchedRule.ID == renameRuleID.Value);

					if (bRename && bInRenameScope && check.Result == ENameCheckResult.Blocked)
					{
						string strReplacement = await GenerateReplacementName(db);

						bRenamed = !String.IsNullOrEmpty(strReplacement) && await global::Database.NameFilter.ForceRename(db, user.ID, strReplacement);
						if (bRenamed)
						{
							++result.NumRenamed;
							Console.WriteLine($"[NAMEFILTER] {strActor} renamed user {user.ID} from '{strName}' to '{strReplacement}' (rule {(check.MatchedRule != null ? check.MatchedRule.ID : -1)})");
						}
					}

					if (result.Samples.Count < ScanSampleCount)
					{
						result.Samples.Add($"`{user.ID}` {strName} ({check.Result}, rule {(check.MatchedRule != null ? check.MatchedRule.ID : -1)}{(bRenamed ? ", renamed" : String.Empty)})");
					}
				}

				await global::Database.NameFilter.LogScanHits(db, lstHits);
			}

			return result;
		}

		// GeneralX, X random rather than derived from the account, so the new name does not
		// advertise which accounts were renamed. Uniqueness is checked because random collides.
		public async Task<string> GenerateReplacementName(AppDbContext db)
		{
			for (int attempt = 0; attempt < ReplacementNameAttempts; ++attempt)
			{
				string strCandidate = String.Format("General{0}", Random.Shared.Next(1000, 1000000));

				if (!await global::Database.NameFilter.IsNameOrSkeletonTaken(db, strCandidate, NameSkeleton.Skeletonize(strCandidate)))
				{
					return strCandidate;
				}
			}

			return String.Empty;
		}

		// The scanreport CSV read back with a verdict column filled in: keep reports an allow line for
		// the rules file, remove renames every account under that name, unsure is left alone.
		public async Task<NameDecisionResult> ApplyDecisions(string strFileName, bool bApply, string strActor)
		{
			NameDecisionResult result = new NameDecisionResult();

			string strPath = Path.Combine("data", "namefilter_decisions", strFileName);
			if (!System.IO.File.Exists(strPath))
			{
				result.Error = $"{strPath} does not exist";
				return result;
			}

			List<Dictionary<string, string>> lstRows = ReadCsv(await System.IO.File.ReadAllLinesAsync(strPath));
			if (lstRows.Count == 0)
			{
				result.Error = "the file has no rows";
				return result;
			}

			await using var db = await m_dbFactory.CreateDbContextAsync();

			foreach (Dictionary<string, string> dictRow in lstRows)
			{
				++result.NumRows;

				dictRow.TryGetValue("verdict", out string? strVerdict);
				dictRow.TryGetValue("sample_name", out string? strName);
				dictRow.TryGetValue("skeleton", out string? strSkeleton);

				if (String.IsNullOrEmpty(strName) || String.IsNullOrEmpty(strSkeleton))
				{
					++result.NumSkipped;
					continue;
				}

				switch ((strVerdict ?? String.Empty).Trim().ToLowerInvariant())
				{
					case "keep":
						result.AllowLines.Add($"allow\texact\t{strName}\ttriage");
						break;

					case "remove":
						List<Int64> lstUserIDs = await global::Database.NameFilter.GetScanUserIDsBySkeleton(db, strSkeleton);
						result.NumRenamed += lstUserIDs.Count;

						// counted either way, applied only on confirm
						if (bApply)
						{
							foreach (Int64 userID in lstUserIDs)
							{
								string strReplacement = await GenerateReplacementName(db);
								if (String.IsNullOrEmpty(strReplacement))
								{
									++result.NumFailed;
									continue;
								}

								if (await global::Database.NameFilter.ForceRename(db, userID, strReplacement))
								{
									Console.WriteLine($"[NAMEFILTER] {strActor} renamed user {userID} from '{strName}' to '{strReplacement}' (decision file {strFileName})");
								}
								else
								{
									++result.NumFailed;
								}
							}
						}
						break;

					case "unsure":
						++result.NumUnsure;
						break;

					default:
						++result.NumSkipped;
						break;
				}
			}

			return result;
		}

		private static List<Dictionary<string, string>> ReadCsv(string[] strLines)
		{
			List<Dictionary<string, string>> lstRows = new();

			if (strLines.Length < 2)
			{
				return lstRows;
			}

			List<string> lstHeaders = SplitCsvLine(strLines[0]);

			for (int i = 1; i < strLines.Length; ++i)
			{
				if (strLines[i].Trim().Length == 0)
				{
					continue;
				}

				List<string> lstFields = SplitCsvLine(strLines[i]);
				Dictionary<string, string> dictRow = new();

				for (int field = 0; field < lstHeaders.Count && field < lstFields.Count; ++field)
				{
					dictRow[lstHeaders[field].Trim().ToLowerInvariant()] = lstFields[field];
				}

				lstRows.Add(dictRow);
			}

			return lstRows;
		}

		private static List<string> SplitCsvLine(string strLine)
		{
			List<string> lstFields = new();
			StringBuilder builder = new StringBuilder();
			bool bInQuotes = false;

			for (int i = 0; i < strLine.Length; ++i)
			{
				char c = strLine[i];

				if (bInQuotes)
				{
					if (c == '"')
					{
						if (i + 1 < strLine.Length && strLine[i + 1] == '"')
						{
							builder.Append('"');
							++i;
						}
						else
						{
							bInQuotes = false;
						}
					}
					else
					{
						builder.Append(c);
					}
				}
				else if (c == '"')
				{
					bInQuotes = true;
				}
				else if (c == ',')
				{
					lstFields.Add(builder.ToString());
					builder.Clear();
				}
				else
				{
					builder.Append(c);
				}
			}

			lstFields.Add(builder.ToString());

			return lstFields;
		}

		// One row per distinct skeleton rather than per user - a decision on a skeleton covers
		// every account that folds onto it.
		public async Task<string> BuildScanCsv(int? ruleID, int limit)
		{
			await using var db = await m_dbFactory.CreateDbContextAsync();

			List<NameScanSkeletonGroup> lstGroups = await global::Database.NameFilter.GetScanSkeletons(db, ruleID, limit);

			StringBuilder builder = new StringBuilder();
			// verdict is left empty on purpose - this file is meant to come back with it filled in
			builder.AppendLine("skeleton,sample_name,accounts,severity,rule_id,rule_pattern,rule_category,match_type,verdict");

			foreach (NameScanSkeletonGroup group in lstGroups)
			{
				NameCheck check = Check(group.SampleName);

				builder.Append(CsvField(group.Skeleton)).Append(',');
				builder.Append(CsvField(group.SampleName)).Append(',');
				builder.Append(group.NumUsers).Append(',');
				builder.Append(GetSeverity(check)).Append(',');
				builder.Append(check.MatchedRule != null ? check.MatchedRule.ID : -1).Append(',');
				builder.Append(CsvField(check.MatchedRule != null ? check.MatchedRule.Pattern : String.Empty)).Append(',');
				builder.Append(CsvField(check.MatchedRule != null ? check.MatchedRule.Category : "structural")).Append(',');
				builder.Append(check.MatchedRule != null ? check.MatchedRule.MatchType.ToString() : check.Result.ToString());
				builder.Append(',');
				builder.AppendLine();
			}

			return builder.ToString();
		}

		private static string CsvField(string strValue)
		{
			// names can contain commas, quotes and the odd control character
			string strClean = strValue.Replace("\r", String.Empty).Replace("\n", " ");

			return "\"" + strClean.Replace("\"", "\"\"") + "\"";
		}
	}
}
