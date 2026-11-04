using System.Text.RegularExpressions;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using VoiceType.Uno.Localization;

namespace VoiceType.Uno.Presentation;

public sealed partial class HelpDialog : ContentDialog
{
    public HelpDialog()
    {
        InitializeComponent();
        Fill(StartBody, "Help.Start.Body");
        Fill(HotkeyBody, "Help.Hotkey.Body");
        Fill(LinuxInjectionBody, "Help.LinuxInjection.Body");
        Fill(AudioBody, "Help.Audio.Body");
        Fill(DataBody, "Help.Data.Body");
    }

    // **bold** and *italic* spans of a help text; whole sentences stay in one string so translators can reorder them.
    private static readonly Regex Emphasis = new(@"\*\*(.+?)\*\*|\*(.+?)\*", RegexOptions.CultureInvariant);

    /// <summary>Fills <paramref name="target"/> with the text of <paramref name="key"/>: "\n" is a line break, **x** bold, *x* italic.</summary>
    private static void Fill(TextBlock target, string key)
    {
        target.Inlines.Clear();
        var lines = Loc.T(key).Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (i > 0)
                target.Inlines.Add(new LineBreak());

            var line = lines[i];
            var at = 0;
            foreach (Match m in Emphasis.Matches(line))
            {
                if (m.Index > at)
                    target.Inlines.Add(new Run { Text = line[at..m.Index] });
                var run = new Run();
                if (m.Groups[1].Success)
                {
                    run.Text = m.Groups[1].Value;
                    run.FontWeight = FontWeights.SemiBold;
                }
                else
                {
                    run.Text = m.Groups[2].Value;
                    run.FontStyle = global::Windows.UI.Text.FontStyle.Italic;
                }
                target.Inlines.Add(run);
                at = m.Index + m.Length;
            }
            if (at < line.Length)
                target.Inlines.Add(new Run { Text = line[at..] });
        }
    }
}
