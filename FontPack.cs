using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Media;

namespace DynamicIsland;

/// <summary>
/// The fonts there are to draw with: SF Pro, which the exe carries itself as resources, and the loose
/// <c>.ttf</c>/<c>.otf</c> files handed over on the settings page, whose copies live in a folder of ours. Nothing gets
/// installed into Windows, so the island leaves no trace outside its own folder, and every chain ends in Segoe UI so
/// that a font missing Cyrillic or the digits costs a glyph rather than a hole.
/// </summary>
static class FontPack
{
    /// <summary>The families packed into the exe as resources, in the order the island drew with them before.</summary>
    public static string[] Bundled => ["SF Pro Text", "SF Pro Display"];

    const string Tail = "Segoe UI Variable Display, Segoe UI";

    /// <summary>
    /// The root the packed fonts are read from. A <c>pack:</c> location only resolves when it is handed over as the
    /// base of the two-argument constructor; written inside the source string it is ignored and the fallbacks take
    /// over, which would quietly strip SF Pro from every chain.
    /// </summary>
    static readonly Uri Exe = new("pack://application:,,,/");

    static readonly Dictionary<string, FontFamily> Faces = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Where the copies of the chosen files live. Made when first asked for, since the app runs fine without it.</summary>
    public static string Folder
    {
        get
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DynamicIsland", "Fonts");
            try
            {
                Directory.CreateDirectory(dir); // already there is not an error
            }
            catch (Exception ex)
            {
                App.Log(ex); // a folder we cannot make costs the custom fonts, nothing else
            }
            return dir;
        }
    }

    /// <summary>
    /// Copies the chosen files in, overwriting a file of the same name. Gives back the families that became
    /// available and a line per file that was refused or could not be read.
    /// </summary>
    public static (string[] Families, string[] Failed) Import(string[] paths)
    {
        var gained = new List<string>();
        var failed = new List<string>();
        foreach (var path in paths ?? [])
        {
            var name = Path.GetFileName(path);
            try
            {
                if (!File.Exists(path))
                {
                    failed.Add($"{name}: the file is not there anymore");
                    continue;
                }
                if (!FontNames.LooksLikeFont(path)) // a collection or a web font would only sit in the folder unused
                {
                    failed.Add($"{name}: not a .ttf or .otf font");
                    continue;
                }

                var copied = Path.Combine(Folder, name);
                File.Copy(path, copied, true);
                foreach (var family in FontNames.Families(copied))
                    if (!gained.Contains(family, StringComparer.OrdinalIgnoreCase)) gained.Add(family);
            }
            catch (Exception ex)
            {
                App.Log(ex);
                failed.Add($"{name}: {ex.Message}");
            }
        }

        Faces.Clear();
        return ([.. gained], [.. failed]);
    }

    /// <summary>
    /// Deletes the copies that exist only to give this one family; a file carrying several of them stays, since
    /// removing it would take the others with it. The families inside the exe are never touched, so asking for one of
    /// them just gives back false.
    /// </summary>
    public static bool Remove(string family)
    {
        if (string.IsNullOrEmpty(family)) return false;

        bool gone = false;
        foreach (var file in Files())
        {
            try
            {
                var carried = FontNames.Families(file);
                if (!carried.Contains(family, StringComparer.OrdinalIgnoreCase)) continue;
                if (carried.Length > 1 && carried.Any(c => !c.Equals(family, StringComparison.OrdinalIgnoreCase))) continue;
                File.Delete(file);
                gone = true;
            }
            catch (Exception ex)
            {
                App.Log(ex);
            }
        }

        Faces.Clear();
        return gone;
    }

    /// <summary>Every family there is to draw with: the bundled SF Pro first, then the imported ones in order.</summary>
    public static string[] Families()
    {
        var list = new List<string>(Bundled);
        foreach (var family in Files().SelectMany(FontNames.Families)
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderBy(f => f, StringComparer.CurrentCultureIgnoreCase))
            if (!list.Contains(family, StringComparer.OrdinalIgnoreCase)) list.Add(family);
        return [.. list];
    }

    /// <summary>
    /// The face to draw with. Nothing chosen, a bundled name, or a family whose file has gone gives back the SF Pro
    /// chain the island has always been covered in — a leftover setting should fall back quietly rather than throw
    /// while text is being measured. A family of its own comes first, with SF Pro and then Segoe UI behind it for the
    /// glyphs and weights it cannot supply. Built once per family and kept, since it is asked for whenever a row is
    /// drawn and a rebuilt <see cref="FontFamily"/> would drop the glyphs WPF has already measured.
    /// </summary>
    public static FontFamily Face(string? family) => Face(family, Packed(family));

    /// <summary>
    /// The same, told which of the packed families leads behind a loose one: the island draws its big text in
    /// SF Pro Display and the rest in SF Pro Text, and a custom face should fall back on the one it replaces.
    /// </summary>
    public static FontFamily Face(string? family, string packed)
    {
        var key = $"{family ?? ""}|{packed}";
        lock (Faces)
        {
            if (Faces.TryGetValue(key, out var known)) return known;

            FontFamily built;
            try
            {
                built = IsLoose(family) ? LooseFace(family!, packed) : PackedFace(Packed(family));
            }
            catch (Exception ex)
            {
                App.Log(ex); // text without a face is text unreadable; segoe is always there to fall back on
                built = new FontFamily(Tail);
            }

            if (Faces.Count > 32) Faces.Clear(); // the settings page walks the whole list; no need to keep it forever
            Faces[key] = built;
            return built;
        }
    }

    /// <summary>What the settings page calls the choice: the family itself, or SF Pro when nothing is.</summary>
    public static string Preview(string? family) =>
        string.IsNullOrWhiteSpace(family) || Bundled.Contains(family, StringComparer.OrdinalIgnoreCase) ? "SF Pro" : family;

    /// <summary>
    /// A chain of nothing but what the exe carries, named twice over: after the <c>#</c> is the copy inside the exe,
    /// on its own the same name as a font installed for the whole system, which is the one to use when somebody has
    /// installed SF Pro properly.
    /// </summary>
    static FontFamily PackedFace(string name) => new(Exe, $"./Fonts/#{name}, {name}, {Tail}");

    /// <summary>
    /// The chosen copy first — spelled as an absolute <c>file:</c> location, which is the one form a loose folder
    /// travels in alongside packed entries — then the families inside the exe, then Segoe UI.
    /// </summary>
    static FontFamily LooseFace(string family, string packed) => new(Exe,
        $"{Location()}#{family}, {PackedChain(packed)}, {Tail}");

    /// <summary>The family to lead with when the choice is one of the bundled ones, or no choice at all.</summary>
    static string Packed(string? family) =>
        Bundled.Contains(family ?? "", StringComparer.OrdinalIgnoreCase) ? family! : Bundled[0];

    /// <summary>Both copies the exe carries, the chosen one first, each reachable as a resource and as a system font.</summary>
    static string PackedChain(string? family)
    {
        var first = Packed(family);
        var rest = Bundled.Where(b => !b.Equals(first, StringComparison.OrdinalIgnoreCase));
        return string.Join(", ", new[] { $"./Fonts/#{first}, {first}" }
            .Concat(rest.Select(b => $"./Fonts/#{b}, {b}")));
    }

    /// <summary>The folder the copies live in, as a uri: forward slashes, spaces escaped, one slash before the name.</summary>
    static string Location() => new Uri(Folder + Path.DirectorySeparatorChar).AbsoluteUri;

    /// <summary>Whether the family is one of the imported ones, whose files sit in our folder rather than in the exe.</summary>
    static bool IsLoose(string? family) => !string.IsNullOrWhiteSpace(family)
        && !Bundled.Contains(family, StringComparer.OrdinalIgnoreCase)
        && Families().Contains(family, StringComparer.OrdinalIgnoreCase);

    static string[] Files()
    {
        try
        {
            return Directory.EnumerateFiles(Folder)
                .Where(f => FontNames.Extensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception ex)
        {
            App.Log(ex);
            return [];
        }
    }
}
