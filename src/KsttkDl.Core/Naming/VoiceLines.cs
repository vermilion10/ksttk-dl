namespace KsttkDl.Core.Naming;

/// <summary> Ship voice line numbering and filenames. The filename formula is separate from <see cref="AssetPath"/>'s CG hash - it is KC3Kai's <c>Meta.getFilenameByVoiceLine</c>, driven by <c>workingDiffs</c>. The number-to-label map is kc3-translations' <c>tools/convert-quotes.py</c>, which is what produces the keys in quotes.json. </summary>
public static class VoiceLines
{
    private static readonly int[] WorkingDiffs =
    [
        2475, 6547, 1471, 8691, 7847, 3595, 1767, 3311, 2507,
        9651, 5321, 4473, 7117, 5947, 9489, 2669, 8741, 6149,
        1301, 7297, 2975, 6413, 8391, 9705, 2243, 2091, 4231,
        3107, 9499, 4205, 6013, 3393, 6401, 6985, 3683, 9447,
        3287, 5181, 7587, 9353, 2135, 4947, 5405, 5223, 9457,
        5767, 9265, 8191, 3927, 3061, 2805, 3273, 7331,
    ];

    /// <summary> Ships with a special attack cut-in CG and its voice lines. KC3Kai's  <c>specialCutinIds</c> is broader, but this is the set preload.js enumerates assets for and whose 900-series files are known to exist. </summary>
    public static readonly int[] SpecialCg = [541, 571, 573, 576, 601, 1496];

    /// <summary>Ships that actually have line 6 ("Repair"); most 404 for it.</summary>
    public static readonly int[] SpecialRepairVoiceShips =
    [
        56, 160, 224, 65, 194, 268, 114, 200, 290, 123, 142,
        295, 126, 398, 127, 399, 135, 304, 136, 418, 496,
    ];

    /// <summary>Graf Zeppelin (and Kai) use literal filenames 917/918 for night battle lines.</summary>
    public static readonly int[] SpecialNightBattleShips = [432, 353];

    /// <summary> Lines every friendly ship is expected to have, in preload.js's request order - which is also the order the labels appear in quotes.json.  </summary>
    public static readonly int[] BaseLines =
    [
        1, 25, 2, 3, 4, 28, 24, 8, 13, 9, 10, 26, 27, 11,
        12, 5, 7, 14, 15, 16, 18, 17, 23, 19, 20, 21, 22,
    ];

    /// <summary>Hourly notifications, gated behind <c>api_voicef &amp; 2</c>.</summary>
    public static readonly int[] HourlyLines = Enumerable.Range(30, 24).ToArray();

    private static readonly Dictionary<int, string> Labels = new()
    {
        [1] = "Intro",
        [25] = "Library",
        [2] = "Poke(1)",
        [3] = "Poke(2)",
        [4] = "Poke(3)",
        [28] = "Married",
        [24] = "Wedding",
        [8] = "Ranking",
        [13] = "Join",
        [9] = "Equip(1)",
        [10] = "Equip(2)",
        [26] = "Equip(3)",
        [27] = "Supply",
        [11] = "Docking(1)",
        [12] = "Docking(2)",
        [5] = "Construction",
        [7] = "Return",
        [14] = "Sortie",
        [15] = "Battle",
        [16] = "Attack",
        [18] = "Yasen(1)",
        [17] = "Yasen(2)",
        [23] = "MVP",
        [19] = "Damaged(1)",
        [20] = "Damaged(2)",
        [21] = "Damaged(3)",
        [22] = "Sunk",
        [29] = "Idle",
        [6] = "Repair",
        // Absent from convert-quotes.py's table, but the keys quotes.json uses: 129 is the special idle behind api_voicef & 4, 900-903 the cut-in lines, 917/918 Graf Zeppelin's literal-filename night battle lines.
        [129] = "Idle(2)",
        [900] = "SpCutin",
        [901] = "SpCCutin(1)",
        [902] = "SpCCutin(2)",
        [903] = "SpCCutin(3)",
        [917] = "Yasen(3)",
        [918] = "Yasen(4)",
    };

    /// <summary> Friendly label for a line number (25 -&gt; "Library", 44 -&gt; "H1400"). Unmapped  numbers fall back to "Voice_&lt;n&gt;" so a new line type still lands somewhere. </summary>
    public static string Label(int lineNum)
    {
        if (Labels.TryGetValue(lineNum, out var label))
        {
            return label;
        }

        return lineNum is >= 30 and <= 53 ? $"H{(lineNum - 30):D2}00" : $"Voice_{lineNum}";
    }

    /// <summary> KC3Kai <c>getFilenameByVoiceLine</c>. Lines above 53 use their number as the filename directly instead of being hashed. </summary>
    public static int Filename(int shipId, int lineNum) =>
        lineNum <= 53
            ? (int)(100000 + 17L * (shipId + 7) * WorkingDiffs[lineNum - 1] % 99173)
            : lineNum;

    /// <summary>Every line a ship is expected to have, following preload.js's gating.</summary>
    public static IEnumerable<int> LinesFor(int shipId, int voiceFlag, bool includeRare)
    {
        foreach (var line in BaseLines)
        {
            yield return line;
        }

        if ((voiceFlag & 1) != 0)
        {
            yield return 29;
        }

        if ((voiceFlag & 4) != 0)
        {
            yield return 129;
        }

        if ((voiceFlag & 2) != 0)
        {
            foreach (var line in HourlyLines)
            {
                yield return line;
            }
        }

        if (SpecialCg.Contains(shipId))
        {
            yield return 900;

            if (includeRare)
            {
                yield return 901;
                yield return 902;
                yield return 903;
            }
        }

        if (SpecialNightBattleShips.Contains(shipId))
        {
            yield return 917;
            yield return 918;
        }

        if (SpecialRepairVoiceShips.Contains(shipId))
        {
            yield return 6;
        }
    }
}
