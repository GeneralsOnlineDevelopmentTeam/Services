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

public static class EloConfig
{
    public const int BaseRating = 1000;
    public const int KFactor = 24; // base for per game volatility, increases for first 10 matches, lower after that
    public const int EloExpansionValue_Standard = 50;
    public const int EloExpansionValue_HighELO = 150;
    public const int SecondsBetweenEloExpansionsInMatchmaking = 10;
    public const int HighEloThreshold = 2000;
}

public sealed class EloData(int rating, int monthlyRating, int matchCount)
{
    public int Rating { get; set; } = rating;
    public int NumMatches { get; set; } = matchCount;
    public int MonthlyRating { get; set; } = monthlyRating;

    public EloData()
        : this(EloConfig.BaseRating, EloConfig.BaseRating, 0)
    {
    }

    public EloData(int rating, int numMatches)
        : this(rating, EloConfig.BaseRating, numMatches)
    {
    }
}

public static class Elo
{
    public static void ApplyResult(EloData winner, EloData loser)
    {
        var winnerScore = GetExpectedScore(winner.Rating, loser.Rating);
        var loserScore = 1.0 - winnerScore;

        var winnerKFactor = GetEffectiveKFactor(EloConfig.KFactor, winner.NumMatches);
        var loserKFactor = GetEffectiveKFactor(EloConfig.KFactor, loser.NumMatches);

        winner.Rating += (int)Math.Round(winnerKFactor * (1.0 - winnerScore));
        loser.Rating -= (int)Math.Round(loserKFactor * loserScore);
    }

    private static double GetExpectedScore(int player, int opponent)
    {
        return 1.0 / (1.0 + Math.Pow(10.0, (opponent - player) / 400.0));
    }

    private static int GetEffectiveKFactor(int baseK, int numberOfGames)
    {
        // Brand new players get a higher K factor to
        // allow their rating to adjust more quickly
        if (numberOfGames < 10)
        {
            return baseK * 2;
        }

        // Players with less than 100 games may still improve their game skill
        // and therefore get a slightly higher K factor
        if (numberOfGames< 100)
        {
            return (int) (baseK* 1.25);
        }

        return baseK;
    }
}
