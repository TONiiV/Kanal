using Kanal.Core.Models;

namespace Kanal.Core.Providers.Testing;

/// <summary>
/// Demo translator: returns real translations for the <see cref="FakeAsrProvider.DefaultScript"/>
/// lines so every column reads in its own language, exactly like the live pipeline.
/// Unknown text falls back to a tagged passthrough so custom scripts stay visible.
/// </summary>
public sealed class FakeMtProvider : IMtProvider
{
    private static readonly Dictionary<string, IReadOnlyDictionary<string, string>> ScriptTranslations =
        new()
        {
            ["这批支架的料号是 KX-4402，表面处理按上次的标准做。"] = new Dictionary<string, string>
            {
                ["de"] = "Die Teilenummer dieser Halterungen ist KX-4402, Oberflächenbehandlung nach dem letzten Standard.",
                ["pl"] = "Numer części tych wsporników to KX-4402, obróbka powierzchni według poprzedniego standardu.",
                ["en"] = "The part number for this batch of brackets is KX-4402; surface finish per the previous standard.",
            },
            ["Musimy potwierdzić termin dostawy przed końcem sierpnia."] = new Dictionary<string, string>
            {
                ["zh"] = "我们必须在八月底前确认交货期。",
                ["de"] = "Wir müssen den Liefertermin vor Ende August bestätigen.",
                ["en"] = "We need to confirm the delivery date before the end of August.",
            },
            ["Die Toleranzen im Zeichnungssatz sind noch nicht freigegeben."] = new Dictionary<string, string>
            {
                ["zh"] = "图纸中的公差还没有放行。",
                ["pl"] = "Tolerancje w zestawie rysunków nie zostały jeszcze zatwierdzone.",
                ["en"] = "The tolerances in the drawing set have not been released yet.",
            },
            ["阳极氧化的颜色样品下周一寄出，顺丰到华沙大概四天。"] = new Dictionary<string, string>
            {
                ["de"] = "Die Farbmuster der Eloxierung gehen nächsten Montag raus; mit SF Express nach Warschau etwa vier Tage.",
                ["pl"] = "Próbki kolorów anodowania wyślemy w najbliższy poniedziałek; kurierem SF do Warszawy około czterech dni.",
                ["en"] = "The anodizing colour samples ship next Monday; SF Express to Warsaw takes about four days.",
            },
            ["Czy próbki będą zgodne z normą ISO 7599?"] = new Dictionary<string, string>
            {
                ["zh"] = "样品会符合 ISO 7599 标准吗？",
                ["de"] = "Werden die Muster der Norm ISO 7599 entsprechen?",
                ["en"] = "Will the samples comply with ISO 7599?",
            },
            ["Wir brauchen außerdem das Erstmusterprüfprotokoll für KX-4402."] = new Dictionary<string, string>
            {
                ["zh"] = "另外我们还需要 KX-4402 的首件检验报告。",
                ["pl"] = "Potrzebujemy też raportu z kontroli pierwszej sztuki dla KX-4402.",
                ["en"] = "We also need the initial sample inspection report for KX-4402.",
            },
        };

    private readonly TimeSpan _delay;

    public FakeMtProvider(TimeSpan? delay = null) => _delay = delay ?? TimeSpan.FromMilliseconds(200);

    public string Id => "fake-mt";

    public async Task<IReadOnlyDictionary<string, string>> TranslateAsync(
        string text, string from, IReadOnlyList<string> to,
        IReadOnlyList<Utterance> context, CancellationToken ct)
    {
        await Task.Delay(_delay, ct);
        var known = ScriptTranslations.GetValueOrDefault(text);
        return to.ToDictionary(
            lang => lang,
            lang => known?.GetValueOrDefault(lang) ?? $"[{from}→{lang}] {text}");
    }
}
