using System.Windows;
using VoiceType.Services;
using VoiceType.ViewModels;

namespace VoiceType.Views;

/// <summary>Dialog asking which remembered window/field to type into.</summary>
public partial class ClarifyTargetDialog : Window
{
    private readonly ClarifyTargetViewModel _vm;

    public ClarifyTargetDialog(string appName, IReadOnlyList<WindowTarget> candidates)
    {
        InitializeComponent();
        _vm = new ClarifyTargetViewModel(appName, candidates);
        DataContext = _vm;
        _vm.SelectionConfirmed += () => { DialogResult = true; Close(); };
    }

    public WindowTarget? SelectedTarget => _vm.SelectedTarget;

    /// <summary>Route a recognized voice answer into the dialog. Returns true when it made a selection.</summary>
    public bool TryHandleVoice(string text) => _vm.TrySelectByVoice(text);

    protected override void OnKeyDown(System.Windows.Input.KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == System.Windows.Input.Key.Escape)
        {
            DialogResult = false;
            Close();
        }
    }
}
