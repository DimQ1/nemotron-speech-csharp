namespace VoiceType.WinUI.Interfaces;
using VoiceType.WinUI.Models;
using VoiceType.WinUI.Services;

public interface IPostProcessingPipeline
{
    IReadOnlyList<PostProcessingPipeline.CompiledRule> CompileRules(List<PostProcessingRule> rules, bool enabled);
    string Process(string raw, IReadOnlyList<PostProcessingPipeline.CompiledRule> rules);
    string Process(string raw, List<PostProcessingRule> rules, bool enabled);
    string ProcessFinal(string raw, IReadOnlyList<PostProcessingPipeline.CompiledRule> rules);
}
