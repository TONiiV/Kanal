using Kanal.Core.Meetings;

namespace Kanal.Core.UnitTests;

public class MeetingTitlingTests
{
    private sealed class Titler : IMeetingTitler
    {
        private readonly TaskCompletionSource<string?> _held = new();

        public string? Next { get; set; } = "Tolerance review";

        public Exception? Throws { get; set; }

        public bool Hold { get; set; }

        public int Calls { get; private set; }

        public IReadOnlyList<string> Saw { get; private set; } = [];

        public void Release(string? title) => _held.SetResult(title);

        public async Task<string?> SuggestAsync(IReadOnlyList<string> lines, CancellationToken ct)
        {
            Calls++;
            Saw = lines;
            if (Throws is not null)
                throw Throws;
            if (!Hold)
                return Next;

            await using (ct.Register(() => _held.TrySetCanceled(ct)))
                return await _held.Task;
        }
    }

    private static string[] Said(int count) =>
        [.. Enumerable.Range(1, count).Select(i => $"line {i}")];

    private static MeetingTitling Titling(Titler? titler) => new(() => titler, minimumLines: 4);

    [Fact]
    public async Task ATitleIsNotGuessedFromTooLittleToGoOn()
    {
        var titler = new Titler();
        var titling = Titling(titler);

        await titling.OfferAsync(Said(3));

        Assert.Equal(0, titler.Calls);
        Assert.Null(titling.Title);

        await titling.OfferAsync(Said(4));

        Assert.Equal(1, titler.Calls);
        Assert.Equal("Tolerance review", titling.Title);
    }

    [Fact]
    public async Task TheAutomaticSuggestionHappensOnceAndNotOnEveryFinal()
    {
        var titler = new Titler();
        var titling = Titling(titler);

        await titling.OfferAsync(Said(4));
        await titling.OfferAsync(Said(9));
        await titling.OfferAsync(Said(30));

        Assert.Equal(1, titler.Calls);
    }

    [Fact]
    public async Task ANameTheOperatorTypedIsNeverOverwrittenByASuggestion()
    {
        var titler = new Titler();
        var titling = Titling(titler);

        titling.Rename("Werkzeugübergabe");
        await titling.OfferAsync(Said(20));

        Assert.Equal(0, titler.Calls);
        Assert.Equal("Werkzeugübergabe", titling.Title);
        Assert.True(titling.NamedByHand);
    }

    [Fact]
    public async Task RegenerationIsTheOneWayAHandTypedNameIsReplaced()
    {
        var titler = new Titler();
        var titling = Titling(titler);
        titling.Rename("Werkzeugübergabe");

        titler.Next = "Delivery split";
        await titling.RegenerateAsync(Said(20));

        Assert.Equal("Delivery split", titling.Title);
        Assert.False(titling.NamedByHand);
    }

    [Fact]
    public async Task AResultThatArrivesAfterTheOperatorTypedIsDropped()
    {
        var titler = new Titler { Hold = true };
        var titling = Titling(titler);

        var running = titling.OfferAsync(Said(6));
        titling.Rename("Werkzeugübergabe");
        titler.Release("Tolerance review");
        await running;

        Assert.Equal("Werkzeugübergabe", titling.Title);
        Assert.True(titling.NamedByHand);
    }

    [Fact]
    public async Task AFailedOrCancelledSuggestionLeavesAUsableTitle()
    {
        var titler = new Titler { Throws = new InvalidOperationException("no model") };
        var titling = Titling(titler);

        await titling.OfferAsync(Said(6));
        Assert.Null(titling.Title);
        Assert.False(titling.IsSuggesting);

        var held = new Titler { Hold = true };
        var second = Titling(held);
        var running = second.OfferAsync(Said(6));
        second.Cancel();
        await running;

        Assert.Null(second.Title);
        Assert.False(second.IsSuggesting);
    }

    [Fact]
    public async Task AnEmptyResultIsNoTitleAtAllRatherThanABlankOne()
    {
        var titler = new Titler { Next = "   " };
        var titling = Titling(titler);

        await titling.OfferAsync(Said(6));

        Assert.Null(titling.Title);
    }

    [Fact]
    public void RenamingToNothingIsNotARename()
    {
        var titling = Titling(new Titler());

        titling.Rename("  ");

        Assert.Null(titling.Title);
        Assert.False(titling.NamedByHand);
    }

    [Fact]
    public async Task TheNextMeetingStartsWithoutTheLastOnesName()
    {
        var titler = new Titler();
        var titling = Titling(titler);
        titling.Rename("Werkzeugübergabe");

        titling.Reset();

        Assert.Null(titling.Title);
        Assert.False(titling.NamedByHand);

        await titling.OfferAsync(Said(6));
        Assert.Equal("Tolerance review", titling.Title);
    }

    [Fact]
    public async Task WithNoModelBehindItNothingIsGuessedAndNothingBreaks()
    {
        var titling = Titling(null);

        await titling.OfferAsync(Said(20));
        await titling.RegenerateAsync(Said(20));

        Assert.False(titling.CanSuggest);
        Assert.Null(titling.Title);
        Assert.False(titling.Failed);
    }

    [Fact]
    public async Task AFailedAttemptSaysSoRatherThanLookingLikeItNeverRan()
    {
        var titler = new Titler { Throws = new InvalidOperationException("no model") };
        var titling = Titling(titler);

        await titling.OfferAsync(Said(6));
        Assert.True(titling.Failed);

        titler.Throws = null;
        await titling.RegenerateAsync(Said(6));
        Assert.False(titling.Failed);
        Assert.Equal("Tolerance review", titling.Title);
    }

    [Fact]
    public async Task TheModelIsShownTheWordsAndNothingElse()
    {
        var titler = new Titler();
        var titling = Titling(titler);

        await titling.OfferAsync(Said(6));

        Assert.Equal(Said(6), titler.Saw);
    }
}
