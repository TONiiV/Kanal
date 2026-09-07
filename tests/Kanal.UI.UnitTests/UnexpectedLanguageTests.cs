using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Kanal.Core.Providers.Testing;

namespace Kanal.UI.UnitTests;

/// <summary>
/// Somebody speaks a language nobody picked. The room still has three columns, and none of them
/// may claim to be the original — the transcript belongs to the language that was recognised.
/// </summary>
public class UnexpectedLanguageTests
{
    private static async Task WaitForAsync(Func<bool> condition, int timeoutMs = 15_000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline)
                throw new TimeoutException("Condition not met in time.");
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(50);
        }

        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public async Task ALanguageNobodyPickedIsNamedRatherThanBorrowingAColumn()
    {
        var vm = TestViewModels.Demo();
        vm.PlanFilter = plan => plan with
        {
            Asr = new FakeAsrProvider(
                script: [new FakeAsrProvider.Line("S01", "en", "We need the ISO 7599 samples before Friday.")],
                loop: true),
        };

        await vm.StartCommand.ExecuteAsync(null);
        await WaitForAsync(() => vm.Columns.All(c => c.Bubbles.Any(b => !b.IsPartial && !b.AwaitingTranslation)));

        Assert.Equal(["zh", "de", "pl"], vm.Columns.Select(c => c.Language));
        foreach (var column in vm.Columns)
        {
            var bubble = column.Bubbles.First(b => !b.IsPartial && !b.AwaitingTranslation);
            Assert.Equal("EN", bubble.SourceLang);
            Assert.False(bubble.IsTranscript);
            // Nobody reads English here, but the words that were actually said stay on screen.
            Assert.Equal("We need the ISO 7599 samples before Friday.", bubble.SourceText);
        }

        await vm.StopCommand.ExecuteAsync(null);
    }
}
