namespace VoiceType.Uno.Services;

/// <summary>Adaptive state of the main screen (design §5.6).</summary>
public enum MainLayoutState
{
    /// <summary>Default phone portrait: cards stacked.</summary>
    Narrow,

    /// <summary>Width ≥ 840: cards side by side, wider side padding.</summary>
    Wide,

    /// <summary>Height &lt; 480 (phone landscape): compact top bar and Record button.</summary>
    Short,

    /// <summary>Height ≤ 520 and width &lt; 560 with translation on (split screen): one card slot with a switch.</summary>
    Compact,

    /// <summary>On-screen keyboard open: compact command bar, translation collapsed to its header.</summary>
    Keyboard
}

/// <summary>How the transcript and translation cards share the content row.</summary>
public enum CardArrangement
{
    /// <summary>Translation is off: the transcript fills the row.</summary>
    TranscriptOnly,

    /// <summary>Transcript above translation (1* / 0.8*).</summary>
    Stacked,

    /// <summary>Two equal columns.</summary>
    SideBySide,

    /// <summary>One slot; a Transcript | Translation switch picks the visible card.</summary>
    Tabbed,

    /// <summary>Transcript above a translation card collapsed to its header.</summary>
    TranslationCollapsed
}

/// <summary>Where the audio source chip sits (design voicetype-uno-mobile §5.2).</summary>
public enum SourceChipPlacement
{
    /// <summary>Only one source exists (Android: the microphone), so there is nothing to show or switch.</summary>
    None,

    /// <summary>A 44 px row under the app bar: icon, short name and chevron.</summary>
    Row,

    /// <summary>An icon-only 44 px button in the app bar, where a whole row would not fit (low height, keyboard).</summary>
    TopBar
}

/// <param name="State">Named adaptive state.</param>
/// <param name="Cards">Placement of the two cards.</param>
/// <param name="IsCompactChrome">48 px top bar, 56 px Record button without a caption.</param>
/// <param name="IsCompactBanner">One-line banners and a progress strip without its caption.</param>
/// <param name="SidePadding">Horizontal padding of the banners and the content row.</param>
/// <param name="SourceChip">Where the audio source chip is shown.</param>
public readonly record struct MainLayout(
    MainLayoutState State,
    CardArrangement Cards,
    bool IsCompactChrome,
    bool IsCompactBanner,
    double SidePadding,
    SourceChipPlacement SourceChip = SourceChipPlacement.Row)
{
    public const double WideMinWidth = 840;
    public const double ShortMaxHeight = 480;     // exclusive
    public const double SideBySideMinWidth = 560;
    public const double CompactMaxHeight = 520;   // inclusive

    /// <summary>
    /// Smallest page height that has room for the source chip as a row. Below it the stacked cards, banners and command bar
    /// of a 320x568 phone (page height about 496 px after the system bars) no longer keep their minimum heights.
    /// </summary>
    public const double SourceRowMinHeight = 560;

    /// <summary>
    /// Picks the layout for a window size. Width and height triggers are combined here
    /// rather than in XAML adaptive triggers, which cannot express "height ≤ 520 and
    /// width &lt; 560" or the keyboard state.
    /// </summary>
    /// <param name="hasSourceChoice">False where only one audio source exists: no chip at all.</param>
    public static MainLayout Compute(double width, double height, bool isTranslationVisible, bool isKeyboardOpen, bool hasSourceChoice = true)
    {
        var isShort = height < ShortMaxHeight;
        var isLowHeight = height <= CompactMaxHeight;
        var sidePadding = width >= WideMinWidth ? 24 : 16;

        MainLayoutState state;
        if (isKeyboardOpen)
            state = MainLayoutState.Keyboard;
        else if (isTranslationVisible && isLowHeight && width < SideBySideMinWidth)
            state = MainLayoutState.Compact;
        else if (isShort)
            state = MainLayoutState.Short;
        else if (width >= WideMinWidth)
            state = MainLayoutState.Wide;
        else
            state = MainLayoutState.Narrow;

        var cards = !isTranslationVisible
            ? CardArrangement.TranscriptOnly
            : state switch
            {
                MainLayoutState.Keyboard => CardArrangement.TranslationCollapsed,
                MainLayoutState.Compact => CardArrangement.Tabbed,
                // Short with translation on is always at least 560 wide (narrower is Compact).
                MainLayoutState.Short or MainLayoutState.Wide => CardArrangement.SideBySide,
                _ => CardArrangement.Stacked
            };

        var sourceChip = !hasSourceChoice
            ? SourceChipPlacement.None
            : isKeyboardOpen || height < SourceRowMinHeight ? SourceChipPlacement.TopBar : SourceChipPlacement.Row;

        return new MainLayout(state, cards, isShort || isKeyboardOpen, isLowHeight, sidePadding, sourceChip);
    }
}
