using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Microsoft.Win32;

namespace DynamicIsland;

public partial class MainWindow
{
    static readonly int[] FontScales = [80, 90, 100, 110, 120, 130, 140, 150, 160];

    readonly Dictionary<DependencyObject, double> _drawnSizes = new();
    FontFamily? _plainFace, _plainDisplay;

    void FontsRow_Click(object sender, RoutedEventArgs e)
    {
        UpdateFonts();
        ShowPanel(Panel.Fonts);
    }

    void SetFace(string family)
    {
        Settings.Font = family;
        ApplyFonts();
    }

    void Font_Click(object sender, RoutedEventArgs e) => SetFace(NextFace(FontPack.Families(), Settings.Font, 1));

    void FontScale_Click(object sender, RoutedEventArgs e) => SetFontScale(Settings.FontScale + 5);

    void SetFontScale(int percent)
    {
        percent = Math.Clamp(percent, FontScales[0], FontScales[^1]);
        if (percent == Settings.FontScale) return;
        Settings.FontScale = percent;
        ApplyFonts();
    }

    void FontAdd_Click(object sender, RoutedEventArgs e)
    {
        var pick = new OpenFileDialog
        {
            Title = "Загрузить шрифт",
            Multiselect = true,
            Filter = "Шрифты (*.ttf;*.otf)|*.ttf;*.otf",
        };
        _pickingFiles = true;
        try
        {
            if (pick.ShowDialog(this) != true) return;
            string[] added = FontPack.Import(pick.FileNames).Families;
            if (added.Length > 0) SetFace(added[^1]);
        }
        finally
        {
            _pickingFiles = false;
        }
    }

    void FontFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(FontPack.Folder);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{FontPack.Folder}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            App.Log(ex);
        }
    }

    void FontReset_Click(object sender, RoutedEventArgs e) => SetFace("");

    void ApplyFonts()
    {
        _plainFace ??= (FontFamily)FindResource("Face");
        _plainDisplay ??= (FontFamily)FindResource("FaceDisplay");
        if (string.IsNullOrWhiteSpace(Settings.Font))
        {
            Resources["Face"] = _plainFace;
            Resources["FaceDisplay"] = _plainDisplay;
        }
        else
        {
            Resources["Face"] = FontPack.Face(Settings.Font, "SF Pro Text");
            Resources["FaceDisplay"] = FontPack.Face(Settings.Font, "SF Pro Display");
        }
        ScaleFonts();
        UpdateFonts();
    }

    void ScaleFonts() => FitFonts(this, Math.Clamp(Settings.FontScale / 100.0, 0.6, 1.7));

    void FitFonts(DependencyObject from, double k)
    {
        foreach (object entry in LogicalTreeHelper.GetChildren(from))
        {
            if (entry is not DependencyObject child) continue;
            if (child is TextBlock or Control)
            {
                double drawn = _drawnSizes.TryGetValue(child, out double kept) ? kept : OwnFontSize(child) ?? double.NaN;
                if (!double.IsNaN(drawn))
                {
                    _drawnSizes[child] = drawn;
                    child.SetValue(TextElement.FontSizeProperty, Math.Round(drawn * k, 2));
                }
            }
            FitFonts(child, k);
        }
    }

    static double? OwnFontSize(DependencyObject node)
    {
        if (node.ReadLocalValue(TextElement.FontSizeProperty) is double local) return local;
        if (node is not FrameworkElement { Style: not null } styled) return null;
        foreach (SetterBase entry in styled.Style.Setters)
            if (entry is Setter { Property: var property, Value: double value } && property == TextElement.FontSizeProperty)
                return value;
        return null;
    }

    void UpdateFonts()
    {
        FontNameText.Text = FontPack.Preview(Settings.Font);
        FontScaleText.Text = Settings.FontScale + "%";
        FontSample.Text = $"{DateTime.Now:HH:mm} Aa Бвг Остров";
    }

    static string NextFace(string[] among, string inUse, int by)
    {
        if (among.Length == 0) return "";
        int at = Array.FindIndex(among, s => string.Equals(s, inUse, StringComparison.OrdinalIgnoreCase));
        return among[(Math.Max(at, 0) + by + among.Length) % among.Length];
    }
}
