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
using GenOnlineService;

public enum MatchResult { PlayerAWins, PlayerBWins }

public static class EloConfig
{
    public static int BaseRating { get; } = 1000;
    public static int KFactor { get; } = 24; // base for per game volatility, increases for first 10 matches, lower after that

    public static int EloExpansionValue_Standard = 50;
	public static int EloExpansionValue_HighELO = 150;
	public static int SecondsBetweenEloExpansionsInMatchmaking = 10;

    public static int HighEloThreshold = 2000;
}

public class EloData
{
    public int Rating { get; set; } = 1000;
    public int NumMatches { get; set; } = 0;
    public int MonthlyRating { get; set; } = 1000;

    public EloData(int rating, int numMatches)
    {
        Rating = rating;
        NumMatches = numMatches;
    }

    public EloData(int rating, int monthlyRating, int numMatches)
    {
        Rating = rating;
        MonthlyRating = monthlyRating;
        NumMatches = numMatches;
    }
}

public static class Elo
{
    public static double ExpectedScore(int ra, int rb)
    {
        // E_A = 1 / (1 + 10^((R_B - R_A)/400))
        return 1.0 / (1.0 + Math.Pow(10.0, (rb - ra) / 400.0));
    }

    public static void ApplyResult(ref EloData playerDataA, ref EloData playerDataB, MatchResult result)
    {
        double ea = ExpectedScore(playerDataA.Rating, playerDataB.Rating);
        double eb = 1.0 - ea;

        double sa = result switch
        {
            MatchResult.PlayerAWins => 1.0,
            MatchResult.PlayerBWins => 0.0,
            _ => 0.5
        };
        double sb = 1.0 - sa;

        int kA = DynamicK(EloConfig.KFactor, playerDataA.NumMatches);
        int kB = DynamicK(EloConfig.KFactor, playerDataB.NumMatches);

        playerDataA.Rating = playerDataA.Rating + (int)Math.Round(kA * (sa - ea));

        playerDataB.Rating = playerDataB.Rating + (int)Math.Round(kB * (sb - eb));
    }

    // note: higher K for new players; dampen after 100 games
    private static int DynamicK(int baseK, int games)
        => games < 10 ? baseK * 2 : (games < 100 ? (int)(baseK * 1.25) : baseK);
}
