using System.Text;
using CrxMoney.Integrations.Countries.Br;

namespace CrxMoney.Integrations.Tools.InstitutionMarks;

/// <summary>The markdown report next to the contact sheet: what the tool did to each mark, and what it left for a person.</summary>
internal static class NormalisationReport
{
    public static string Build(IReadOnlyList<(BrazilianInstitution Institution, Normalisation Normalised, (double OnLight, double OnDark)? Legibility)> marks)
    {
        var report = new StringBuilder();
        report.AppendLine("# Institution marks: normalisation report");
        report.AppendLine();
        report.AppendLine("Written by `tools/InstitutionMarks` with the marks. The contact sheet `br-marks-contact-sheet.png` shows every mark before and after in 44 px boxes, on a light and a dark card.");
        report.AppendLine();

        var review = marks.Where(m => m.Normalised.NeedsReview).ToList();
        report.AppendLine("## Left for manual review");
        report.AppendLine();
        if (review.Count == 0)
        {
            report.AppendLine("None: the tool normalised every SVG it could read, or kept it because its tile is the mark.");
        }
        else
        {
            report.AppendLine("These files are exactly as downloaded. Each reason says what the tool could not decide safely.");
            report.AppendLine();
            report.AppendLine("| COMPE | Institution | Why |");
            report.AppendLine("| --- | --- | --- |");
            foreach (var (institution, normalised, _) in review)
                report.AppendLine($"| {institution.Compe} | {institution.Name} | {normalised.Note} |");
        }

        // The shapes and colours are the institutions': a mark that reads on one card only is listed, never recoloured.
        var poor = marks.Where(m => m.Legibility is { } l && (l.OnLight < 0.7 || l.OnDark < 0.7)).ToList();
        report.AppendLine();
        report.AppendLine("## Reads on one card only");
        report.AppendLine();
        report.AppendLine("Share of each shipped mark's drawn pixels with a contrast of at least 1.6 against the white card and against the dark card (#14161a). The marks are the institutions' and are never recoloured; a mark listed here is for a person to judge on the contact sheet, for instance to give it a light plate in the host.");
        report.AppendLine();
        if (poor.Count == 0)
        {
            report.AppendLine("None.");
        }
        else
        {
            report.AppendLine("| COMPE | Institution | On light | On dark |");
            report.AppendLine("| --- | --- | --- | --- |");
            foreach (var (institution, _, legibility) in poor.OrderBy(m => m.Institution.Compe, StringComparer.Ordinal))
                report.AppendLine($"| {institution.Compe} | {institution.Name} | {legibility!.Value.OnLight:P0} | {legibility.Value.OnDark:P0} |");
        }

        report.AppendLine();
        report.AppendLine("## Every mark");
        report.AppendLine();
        report.AppendLine("| COMPE | Institution | Method | Background dropped | viewBox before | viewBox after | Note |");
        report.AppendLine("| --- | --- | --- | --- | --- | --- | --- |");
        foreach (var (institution, normalised, _) in marks.OrderBy(m => m.Institution.Compe, StringComparer.Ordinal))
        {
            var fill = normalised.BackgroundRemoved && normalised.BackgroundFill is not null ? $"yes ({normalised.BackgroundFill})" : "no";
            report.AppendLine($"| {institution.Compe} | {institution.Name} | {normalised.Method} | {fill} | {normalised.ViewBoxBefore ?? "-"} | {normalised.ViewBoxAfter ?? "-"} | {normalised.Note} |");
        }

        return report.ToString();
    }
}
