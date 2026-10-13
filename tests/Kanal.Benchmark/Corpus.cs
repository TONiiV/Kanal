namespace Kanal.Benchmark;

internal sealed record Sentence(string Lang, string Text, IReadOnlyDictionary<string, string> References);

internal static class Corpus
{
    public static readonly string[] MeetingLanguages = ["zh", "de", "pl"];

    public static IEnumerable<string> Targets(Sentence sentence) =>
        MeetingLanguages.Append("en").Where(l => l != sentence.Lang);

    public static IReadOnlyList<Sentence> Sentences { get; } =
    [
        S("zh", "支架 KB-4471 的焊缝在 2000 次循环后出现裂纹，我们需要重新做疲劳测试。",
            de: "Die Schweißnaht am Halter KB-4471 zeigt nach 2000 Zyklen Risse, wir müssen den Ermüdungstest wiederholen.",
            pl: "W spoinie wspornika KB-4471 po 2000 cyklach pojawiły się pęknięcia, musimy powtórzyć test zmęczeniowy.",
            en: "The weld seam on bracket KB-4471 cracked after 2000 cycles, so we need to repeat the fatigue test."),
        S("zh", "下一批货预计 3 月 15 日从宁波发出，大约需要五周到汉堡。",
            de: "Die nächste Lieferung soll am 15. März in Ningbo verschifft werden und braucht etwa fünf Wochen bis Hamburg.",
            pl: "Następna dostawa ma wyjść z Ningbo 15 marca i dotrze do Hamburga po około pięciu tygodniach.",
            en: "The next shipment is expected to leave Ningbo on March 15 and will take about five weeks to reach Hamburg."),
        S("zh", "我们可以把壁厚从 2.5 毫米减到 2 毫米，但单价只能降低百分之三。",
            de: "Wir können die Wandstärke von 2,5 mm auf 2 mm reduzieren, aber der Stückpreis sinkt nur um drei Prozent.",
            pl: "Możemy zmniejszyć grubość ścianki z 2,5 mm do 2 mm, ale cena jednostkowa spadnie tylko o trzy procent.",
            en: "We can reduce the wall thickness from 2.5 mm to 2 mm, but the unit price can only drop by three percent."),
        S("zh", "请把材料从 S235JR 换成 X5CrNi18-10，客户要求防护等级达到 IP67。",
            de: "Bitte stellen Sie das Material von S235JR auf X5CrNi18-10 um, der Kunde verlangt Schutzart IP67.",
            pl: "Proszę zmienić materiał z S235JR na X5CrNi18-10, klient wymaga stopnia ochrony IP67.",
            en: "Please change the material from S235JR to X5CrNi18-10, the customer requires protection class IP67."),

        S("de", "Die Bohrung für die Schraube M10 liegt 0,3 mm außerhalb der Toleranz.",
            zh: "M10 螺栓的孔位超出公差 0.3 毫米。",
            pl: "Otwór pod śrubę M10 jest 0,3 mm poza tolerancją.",
            en: "The hole for the M10 screw is 0.3 mm out of tolerance."),
        S("de", "Können Sie uns bis Freitag ein Angebot für 5000 Stück schicken?",
            zh: "您能在周五之前给我们发一份 5000 件的报价吗？",
            pl: "Czy mogą nam Państwo przesłać ofertę na 5000 sztuk do piątku?",
            en: "Can you send us a quote for 5000 pieces by Friday?"),
        S("de", "Laut Prüfbericht PB-2291 erreicht die Beschichtung nur 480 Stunden im Salzsprühtest.",
            zh: "根据检测报告 PB-2291，涂层在盐雾试验中只达到 480 小时。",
            pl: "Według raportu z badań PB-2291 powłoka wytrzymuje w teście mgły solnej tylko 480 godzin.",
            en: "According to test report PB-2291, the coating only reaches 480 hours in the salt spray test."),
        S("de", "Wir brauchen die Zeichnung in Revision C, bevor wir das Werkzeug freigeben.",
            zh: "在批准模具之前，我们需要 C 版图纸。",
            pl: "Potrzebujemy rysunku w rewizji C, zanim zatwierdzimy narzędzie.",
            en: "We need the drawing in revision C before we release the tool."),

        S("pl", "Na linii montażowej w Gliwicach brakuje wsporników KB-4471 od wtorku.",
            zh: "自周二以来，格利维采装配线上一直缺少 KB-4471 支架。",
            de: "Seit Dienstag fehlen an der Montagelinie in Gliwice die Halter KB-4471.",
            en: "The assembly line in Gliwice has been short of KB-4471 brackets since Tuesday."),
        S("pl", "Moment dokręcania śrub ustawiliśmy na 25 Nm, zgodnie z rysunkiem.",
            zh: "我们按图纸将螺栓拧紧力矩设定为 25 Nm。",
            de: "Wir haben das Anzugsmoment der Schrauben laut Zeichnung auf 25 Nm eingestellt.",
            en: "We set the tightening torque of the screws to 25 Nm, as per the drawing."),
        S("pl", "Czy dostawca może przyspieszyć wysyłkę próbek o tydzień?",
            zh: "供应商能否把样品发货提前一周？",
            de: "Kann der Lieferant den Versand der Muster um eine Woche vorziehen?",
            en: "Can the supplier bring the sample shipment forward by one week?"),
        S("pl", "Klient odrzucił partię L-2418, bo uszczelki O-ring nie pasują do rowka.",
            zh: "客户拒收了 L-2418 批次，因为 O 形圈和槽不匹配。",
            de: "Der Kunde hat die Charge L-2418 abgelehnt, weil die O-Ringe nicht in die Nut passen.",
            en: "The customer rejected batch L-2418 because the O-ring seals do not fit the groove."),
    ];

    private static Sentence S(string lang, string text, string? zh = null, string? de = null, string? pl = null, string? en = null)
    {
        var refs = new Dictionary<string, string>();
        if (zh is not null) refs["zh"] = zh;
        if (de is not null) refs["de"] = de;
        if (pl is not null) refs["pl"] = pl;
        if (en is not null) refs["en"] = en;
        return new Sentence(lang, text, refs);
    }
}
