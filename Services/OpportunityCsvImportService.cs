using Microsoft.VisualBasic.FileIO;

namespace HendersonSoftwareLabsAPI.Services;

public sealed class CsvImportException(string message) : Exception(message);

public interface IOpportunityCsvImportService
{
    IReadOnlyList<ActiveProjectImportRequest> ParseActiveProjects(Stream csv);
    IReadOnlyList<BusinessProspectImportRequest> ParseBusinessProspects(Stream csv);
}

public sealed class OpportunityCsvImportService : IOpportunityCsvImportService
{
    public IReadOnlyList<ActiveProjectImportRequest> ParseActiveProjects(Stream csv) =>
        ParseRows(csv, ["title", "description", "source_type"], value => new ActiveProjectImportRequest(
            value("title"), value("description"), NonEmpty(value("source_type")) ?? "ExplicitDemand",
            NullIfEmpty(value("source_name")), NullIfEmpty(value("source_url")),
            ParseDate(value("source_date")), NullIfEmpty(value("external_id"))));

    public IReadOnlyList<BusinessProspectImportRequest> ParseBusinessProspects(Stream csv) =>
        ParseRows(csv, ["business_name", "evidence"], value => new BusinessProspectImportRequest(
            value("business_name"), value("evidence"), NullIfEmpty(value("website_url")),
            NullIfEmpty(value("geography")), NullIfEmpty(value("industry")),
            NullIfEmpty(value("source_name")), NullIfEmpty(value("source_url")),
            ParseDate(value("source_date")), NullIfEmpty(value("external_id"))));

    private static List<T> ParseRows<T>(Stream csv, string[] requiredHeaders, Func<Func<string, string>, T> map)
    {
        var rows = new List<T>();
        try
        {
            using var parser = new TextFieldParser(csv) { TextFieldType = FieldType.Delimited, HasFieldsEnclosedInQuotes = true, TrimWhiteSpace = false };
            parser.SetDelimiters(",");
            var headers = parser.ReadFields()?.Select((value, index) => (value.Trim().ToLowerInvariant(), index)).ToDictionary(x => x.Item1, x => x.index)
                ?? throw new CsvImportException("CSV is missing a header row.");
            if (requiredHeaders.Any(x => !headers.ContainsKey(x)))
                throw new CsvImportException($"CSV requires {string.Join(", ", requiredHeaders)} headers.");
            while (!parser.EndOfData)
            {
                var fields = parser.ReadFields() ?? [];
                if (fields.All(string.IsNullOrWhiteSpace)) continue;
                string Value(string name) => headers.TryGetValue(name, out var index) && index < fields.Length ? fields[index].Trim() : "";
                rows.Add(map(Value));
                if (rows.Count > 100) throw new CsvImportException("CSV imports are limited to 100 records.");
            }
        }
        catch (Exception ex) when (ex is MalformedLineException or InvalidDataException or ArgumentException)
        {
            throw new CsvImportException("CSV could not be parsed. Check quoting and column structure.");
        }
        if (rows.Count == 0) throw new CsvImportException("CSV contains no records.");
        return rows;
    }

    private static DateTime? ParseDate(string value) => DateTimeOffset.TryParse(value, out var parsed) ? parsed.UtcDateTime : null;
    private static string? NullIfEmpty(string value) => string.IsNullOrWhiteSpace(value) ? null : value;
    private static string? NonEmpty(string value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
