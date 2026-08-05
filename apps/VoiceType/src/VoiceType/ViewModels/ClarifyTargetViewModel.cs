using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using VoiceType.Services;

namespace VoiceType.ViewModels;

/// <summary>One candidate target shown in the clarify dialog (1-based index for voice selection).</summary>
public sealed record ClarifyCandidate(int Index, string Label, WindowTarget Target);

/// <summary>
/// ViewModel for the clarify-target dialog: asks which remembered window/field
/// to type into when a "type into &lt;app&gt;" command is ambiguous.
/// </summary>
public sealed class ClarifyTargetViewModel : INotifyPropertyChanged
{
    private int _selectedIndex = -1;

    public ClarifyTargetViewModel(string appName, IReadOnlyList<WindowTarget> candidates)
    {
        Prompt = $"Type into which field of \"{appName}\"?";
        foreach (var (target, i) in candidates.Select((t, i) => (t, i)))
            Candidates.Add(new ClarifyCandidate(i + 1, target.DisplayLabel, target));
    }

    public string Prompt { get; }

    public ObservableCollection<ClarifyCandidate> Candidates { get; } = new();

    /// <summary>ListBox SelectedIndex binding (0-based); -1 = none.</summary>
    public int SelectedIndex
    {
        get => _selectedIndex;
        set
        {
            if (SetProperty(ref _selectedIndex, value) && value >= 0)
                SelectionConfirmed?.Invoke();
        }
    }

    /// <summary>Raised when the user picks a candidate (click or programmatically).</summary>
    public event Action? SelectionConfirmed;

    public WindowTarget? SelectedTarget =>
        _selectedIndex >= 0 && _selectedIndex < Candidates.Count
            ? Candidates[_selectedIndex].Target
            : null;

    /// <summary>Voice selection: "first"/"первое"/"1" picks the 1-based candidate.</summary>
    public bool TrySelectByVoice(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var t = text.Trim().ToLowerInvariant();

        int? number = t switch
        {
            "1" or "first" or "one" or "первое" or "первый" or "первая" or "раз" => 1,
            "2" or "second" or "two" or "второе" or "второй" or "вторая" or "два" => 2,
            "3" or "third" or "three" or "третье" or "третий" or "третья" or "три" => 3,
            "4" or "fourth" or "four" or "четвёртое" or "четвертое" or "четыре" => 4,
            "5" or "fifth" or "five" or "пятое" or "пятый" or "пять" => 5,
            _ => null,
        };

        // Also accept trailing digit, e.g. "поле 2".
        if (number is null)
        {
            var last = t.Split(' ', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
            if (last is not null && int.TryParse(last, out var n))
                number = n;
        }

        if (number is null || number < 1 || number > Candidates.Count)
            return false;

        SelectedIndex = number.Value - 1;
        return true;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
