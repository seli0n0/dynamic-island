using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DynamicIsland;

/// lrclib indexes mostly Western releases, so a Russian track often isn't there at all. NetEase Cloud Music keeps
/// user-made subtitles for the Russian catalogue and answers from a Russian provider, so it serves what lrclib lacks.
sealed partial class LyricsService
{
    const int NetEaseProbes = 3;
    const string NetEaseSearch = "https://music.163.com/api/search/get?type=1&limit=13&s=";
    const string NetEaseLyric = "https://music.163.com/api/song/lyric?lv=-1&kv=-1&tv=-1&id=";
    const string NetEaseCredits = @"作词|作曲|编曲|编词|制作人?|制作|出品|发行|录音|混音|母带|监制|吉他|贝斯|和声|歌词|专辑|纯音乐|请欣赏|Lyrics?\b|Lyricist|Composer|Arranger|Producer|Mixed|Recorded";

    /// NetEase prefixes tracks with credits stamped at zero, which would show as the first sung line, and its search
    /// answers covers and same-titled songs by other artists, whose words would be wrong for the track.
    static readonly Regex Credit = new(@"^\s*(?:\[[^\]]*\]\s*)?(" + NetEaseCredits + @"|[《「].*[》」]).*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    static readonly HttpClient NetEaseHttp = CreateNetEase();

    sealed record Hit(long Id, double Duration, int Score);

    static async Task<Candidate[]> NetEaseAsync(string title, string artist, CancellationToken cancel)
    {
        foreach (string term in Searches(title, artist))
        {
            if (cancel.IsCancellationRequested) break;
            using JsonDocument? json = await GetAsync(NetEaseHttp, NetEaseSearch + Uri.EscapeDataString(term), cancel);
            if (json == null || !json.RootElement.TryGetProperty("result", out JsonElement result)
                || !result.TryGetProperty("songs", out JsonElement songs) || songs.ValueKind != JsonValueKind.Array) continue;

            List<Candidate> found = [];
            foreach (Hit hit in Rank(songs, title, artist))
            {
                if (cancel.IsCancellationRequested) break;
                Candidate? lyric = await LyricAsync(hit, cancel);
                if (lyric == null) continue;
                found.Add(lyric);
                if (found.Count == NetEaseProbes) break;
            }
            if (found.Count > 0) return [.. found];
        }
        return [];
    }

    static async Task<Candidate?> LyricAsync(Hit hit, CancellationToken cancel)
    {
        using JsonDocument? json = await GetAsync(NetEaseHttp, NetEaseLyric + hit.Id, cancel);
        if (json == null || !json.RootElement.TryGetProperty("lrc", out JsonElement lrc)) return null;
        return Frame(hit.Duration, Array.FindAll(Parse(Text(lrc, "lyric")), line => !Credit.IsMatch(line.Text)));
    }

    static IEnumerable<string> Searches(string title, string artist)
    {
        string song = Slim(title), by = SlimBy(artist);
        if (by.Length > 0) yield return by + " " + song;

        int dash = song.IndexOf(TitleSeparator, StringComparison.Ordinal);
        if (dash > 0) yield return Slim(song[..dash]) + " " + Slim(song[(dash + TitleSeparator.Length)..]);
        if (song.Length > 0) yield return song;
    }

    static IEnumerable<Hit> Rank(JsonElement songs, string title, string artist)
    {
        string want = Fold(title), by = Fold(artist);
        var hits = new List<Hit>();
        foreach (JsonElement song in songs.EnumerateArray())
        {
            if (!song.TryGetProperty("id", out JsonElement id) || id.ValueKind != JsonValueKind.Number) continue;

            double seconds = Number(song, "duration") / 1000;
            if (seconds < MinUsualDuration) continue;
            if (by.Length > 0 && !Sings(song, by)) continue;

            string name = Fold(Text(song, "name"));
            int score = name.Length == 0 || want.Length == 0 ? 0 : name == want ? 4 : Starts(name, want) || Starts(want, name) ? 2 : 0;
            if (score == 0) continue;
            hits.Add(new Hit(id.GetInt64(), seconds, score));
        }
        return hits.GroupBy(hit => hit.Id).Select(group => group.OrderByDescending(hit => hit.Score).First())
            .OrderByDescending(hit => hit.Score).ThenBy(hit => hit.Id);
    }

    static bool Sings(JsonElement song, string by) => song.TryGetProperty("artists", out JsonElement list)
        && list.ValueKind == JsonValueKind.Array
        && list.EnumerateArray().Any(artist => Starts(Fold(Text(artist, "name")), by) || Starts(by, Fold(Text(artist, "name"))));

    static bool Starts(string have, string want) => have.Length > 0 && want.Length > 0 && have.StartsWith(want, StringComparison.Ordinal);

    static string Text(JsonElement item, string name) =>
        item.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    static double Number(JsonElement item, string name) =>
        item.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : 0;

    static string Fold(string text) => Space(Punctuation.Replace(Slim(text), " ")).ToLowerInvariant().Replace('ё', 'е');

    static HttpClient CreateNetEase()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(RequestSeconds + 2) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36");
        http.DefaultRequestHeaders.Referrer = new Uri("https://music.163.com/");
        http.DefaultRequestHeaders.Add("Cookie", "os=pc");
        return http;
    }
}
