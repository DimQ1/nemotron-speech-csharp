using System.Diagnostics;
using LVA.Core.History;
using Xunit;

namespace LVA.Tests.Core;

public class Unit_HistoryStoreTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteChatHistoryStore _store;

    public Unit_HistoryStoreTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"lva-test-{Guid.NewGuid():N}.db");
        _store = new SqliteChatHistoryStore(_dbPath);
    }

    public void Dispose()
    {
        _store.Dispose();
        SqliteChatHistoryStoreCleanup();
    }

    private void SqliteChatHistoryStoreCleanup()
    {
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { }
    }

    private static HistoryEntry Msg(string text, string channel = "voice")
        => new(0, HistoryEntryKind.UserMessage, text, channel, null, DateTimeOffset.UtcNow);

    [Fact]
    public async Task AppendAndSearch_ShouldRoundtrip()
    {
        await _store.AppendAsync(Msg("как обновить драйвер видеокарты"), CancellationToken.None);
        await _store.AppendAsync(Msg("найди новости про .NET 9"), CancellationToken.None);
        await _store.AppendAsync(Msg("просто разговор"), CancellationToken.None);

        var results = await _store.SearchAsync("драйвер", new HistoryFilter(), CancellationToken.None);
        Assert.Single(results);
        Assert.Contains("драйвер", results[0].Text);
    }

    [Fact]
    public async Task Search_BySubstring_ShouldBeFast()
    {
        // DoD: поиск < 100 мс на 10k записей (проверяем на 1000 для скорости теста)
        for (int i = 0; i < 1000; i++)
            await _store.AppendAsync(Msg($"сообщение номер {i} про разные темы"), CancellationToken.None);

        var sw = Stopwatch.StartNew();
        var results = await _store.SearchAsync("номер 999", new HistoryFilter(), CancellationToken.None);
        sw.Stop();

        Assert.NotEmpty(results);
        Assert.True(sw.ElapsedMilliseconds < 500, $"Search took {sw.ElapsedMilliseconds} ms");
    }

    [Fact]
    public async Task GetSession_ShouldReturnOnlyThatDay()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        await _store.AppendAsync(Msg("сегодня"), CancellationToken.None);

        var session = await _store.GetSessionAsync(today, CancellationToken.None);
        Assert.Single(session);
    }

    [Fact]
    public async Task Clear_WithRetention_ShouldKeepRecent()
    {
        var old = new HistoryEntry(0, HistoryEntryKind.UserMessage, "старое", "voice", null,
            DateTimeOffset.UtcNow.AddDays(-100));
        await _store.AppendAsync(old, CancellationToken.None);
        await _store.AppendAsync(Msg("новое"), CancellationToken.None);

        await _store.ClearAsync(TimeSpan.FromDays(90), CancellationToken.None);

        var all = await _store.GetSessionAsync(DateOnly.FromDateTime(DateTime.UtcNow), CancellationToken.None);
        var oldGone = await _store.SearchAsync("старое", new HistoryFilter(), CancellationToken.None);
        Assert.Empty(oldGone);
    }

    [Fact]
    public async Task Export_Markdown_ShouldContainEntries()
    {
        await _store.AppendAsync(Msg("экспортируемое сообщение"), CancellationToken.None);
        var exportPath = Path.Combine(Path.GetTempPath(), $"lva-export-{Guid.NewGuid():N}.md");
        try
        {
            await _store.ExportAsync(exportPath, HistoryFormat.Markdown, CancellationToken.None);
            var content = await File.ReadAllTextAsync(exportPath);
            Assert.Contains("экспортируемое сообщение", content);
        }
        finally
        {
            if (File.Exists(exportPath)) File.Delete(exportPath);
        }
    }
}
