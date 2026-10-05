using System.IO;
using System.Text;

namespace DynamicIsland;

/// <summary>
/// A copy from the clipboard given a file of its own, so that words and a picture can stand on the shelf beside the
/// files that were put there from Explorer. Such a copy lies in <c>%LOCALAPPDATA%\DynamicIsland\Shelf</c> under a name
/// taken from what it says. Nothing of the clipboard is written until a copy is put on the shelf on purpose.
/// </summary>
static class ShelfPin
{
    const int NameLength = 48; // signs of the copy that go into the name: a tile reads about half of them
    static readonly char[] Forbidden = Path.GetInvalidFileNameChars();

    /// <summary>The folder the copies lie in, made the first time one is wanted.</summary>
    public static string Folder
    {
        get
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DynamicIsland", "Shelf");
            Directory.CreateDirectory(dir); // already there is not an error
            return dir;
        }
    }

    /// <summary>Writes the copy out.</summary>
    /// <returns>Where it lies. The same copy put twice gives the same file back, so the shelf adds no second tile.</returns>
    public static string Put(ClipBook.Entry entry)
    {
        bool picture = entry.Type == ClipBook.Kind.Image;
        byte[] bytes = picture ? entry.Encoded ?? [] : Encoding.UTF8.GetBytes(entry.Body);
        return Write(Folder, Name(entry, picture), picture ? ".png" : ".txt", bytes);
    }

    static string Name(ClipBook.Entry entry, bool picture)
    {
        string raw = picture ? $"Картинка {entry.Hint}" : entry.Snippet;
        var kept = new StringBuilder(Math.Min(raw.Length, NameLength));
        foreach (char c in raw)
        {
            if (kept.Length == NameLength) break;
            // a slash or a colon in the first line of a copy cannot stand in a file's name
            kept.Append(Array.IndexOf(Forbidden, c) >= 0 ? ' ' : c);
        }
        string name = kept.ToString().Trim().TrimEnd('.', ' ');
        return name.Length > 0 ? name : "Копия"; // signs none of them fit to be read
    }

    static string Write(string folder, string name, string extension, byte[] bytes)
    {
        for (int n = 1; ; n++)
        {
            string path = Path.Combine(folder, n == 1 ? name + extension : $"{name} {n}{extension}");
            if (File.Exists(path) && Same(path, bytes)) return path;
            try
            {
                File.WriteAllBytes(path, bytes);
                return path;
            }
            catch (IOException) when (n < 100) { } // the name has gone to something else in the meanwhile
        }
    }

    static bool Same(string path, byte[] bytes)
    {
        try
        {
            return new FileInfo(path).Length == bytes.Length && File.ReadAllBytes(path).SequenceEqual(bytes);
        }
        catch (IOException)
        {
            return false;
        }
    }
}
