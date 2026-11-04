using VoiceType.Uno.Localization;
using VoiceType.Uno.Services;
using VoiceType.Uno.Services.Updates;

namespace VoiceType.Uno.Presentation;

/// <summary>The "new version" banner of the main screen (GitHub Releases, <see cref="UpdateService"/>).</summary>
public sealed partial class MainViewModel
{
    private UpdatePlan? _updateBanner;

    /// <summary>The update the banner offers (null = no banner).</summary>
    public UpdatePlan? UpdateBannerPlan => _updateBanner;

    public bool IsUpdateBannerVisible => _updateBanner is not null;

    /// <summary>Worded when read, so it follows a language switch.</summary>
    public string UpdateBannerText => _updateBanner is { } plan ? Loc.F("Updates.Banner.Text", plan.Release.Version) : "";

    /// <summary>The start-up check, 15 s after launch so it never competes with loading the model.</summary>
    public async Task CheckForUpdatesOnStartAsync(UpdateService updates)
    {
        updates.Changed += () => _scheduler.Post(() => SetUpdateBanner(
            updates.Available is { } p && !updates.IsSkipped(p) ? p : null));
        Loc.LanguageChanged += () => OnPropertyChanged(nameof(UpdateBannerText));
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(15)).ConfigureAwait(false);
            await updates.CheckOnStartAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[VoiceType.Uno] Start-up update check failed: {ex.Message}");
        }
    }

    /// <summary>Hides the banner for this session (Later / after Skip).</summary>
    public void DismissUpdateBanner() => SetUpdateBanner(null);

    private void SetUpdateBanner(UpdatePlan? plan)
    {
        _updateBanner = plan;
        OnPropertyChanged(nameof(UpdateBannerPlan));
        OnPropertyChanged(nameof(IsUpdateBannerVisible));
        OnPropertyChanged(nameof(UpdateBannerText));
    }
}
