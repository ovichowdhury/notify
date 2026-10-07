using System.Globalization;
using System.Net.Mail;
using ClosedXML.Excel;
using CsvHelper;
using CsvHelper.Configuration;

namespace Notify.Web.Services;

public class ParsedRecipientFile
{
    public List<string> Columns { get; } = new();
    public List<Dictionary<string, string>> Rows { get; } = new();
    public string? EmailColumn { get; set; }
    public string? NameColumn { get; set; }
}

/// <summary>Reads CSV / XLSX files into rows keyed by column header.</summary>
public class RecipientFileParser
{
    private static readonly string[] EmailHeaderCandidates = { "email", "emailaddress", "email_address", "e-mail", "mail", "emailid" };
    private static readonly string[] NameHeaderCandidates = { "name", "fullname", "full_name", "customername", "customer", "contact", "contactname", "firstname", "first_name" };

    public ParsedRecipientFile Parse(Stream stream, string fileName)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        var result = ext switch
        {
            ".csv" or ".txt" => ParseCsv(stream),
            ".xlsx" or ".xlsm" => ParseXlsx(stream),
            _ => throw new InvalidOperationException("Unsupported file type. Please upload a .csv or .xlsx file.")
        };

        if (result.Columns.Count == 0)
            throw new InvalidOperationException("The file has no header row.");

        result.EmailColumn = DetectEmailColumn(result);
        if (result.EmailColumn is null)
            throw new InvalidOperationException("Could not find an email column. Add a column named \"Email\" to the file.");

        result.NameColumn = DetectNameColumn(result.Columns);
        return result;
    }

    public static bool IsValidEmail(string? email)
        => !string.IsNullOrWhiteSpace(email) && email.Contains("@") && MailAddress.TryCreate(email.Trim(), out _);

    private static ParsedRecipientFile ParseCsv(Stream stream)
    {
        var result = new ParsedRecipientFile();
        var config = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            HasHeaderRecord = true,
            MissingFieldFound = null,
            BadDataFound = null,
            TrimOptions = TrimOptions.Trim,
            DetectDelimiter = true
        };

        using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);
        using var csv = new CsvReader(reader, config);

        if (!csv.Read()) return result;
        csv.ReadHeader();
        var headers = (csv.HeaderRecord ?? Array.Empty<string>())
            .Select((h, i) => string.IsNullOrWhiteSpace(h) ? $"Column{i + 1}" : h.Trim())
            .ToList();
        result.Columns.AddRange(headers);

        while (csv.Read())
        {
            var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var isEmpty = true;
            for (var i = 0; i < headers.Count; i++)
            {
                var value = csv.TryGetField<string>(i, out var v) ? (v ?? string.Empty).Trim() : string.Empty;
                if (value.Length > 0) isEmpty = false;
                row[headers[i]] = value;
            }
            if (!isEmpty) result.Rows.Add(row);
        }
        return result;
    }

    private static ParsedRecipientFile ParseXlsx(Stream stream)
    {
        var result = new ParsedRecipientFile();
        using var workbook = new XLWorkbook(stream);
        var sheet = workbook.Worksheets.FirstOrDefault();
        if (sheet is null) return result;

        var range = sheet.RangeUsed();
        if (range is null) return result;

        var headers = new List<string>();
        foreach (var cell in range.FirstRow().Cells())
        {
            var header = cell.GetFormattedString().Trim();
            headers.Add(string.IsNullOrWhiteSpace(header) ? $"Column{headers.Count + 1}" : header);
        }
        result.Columns.AddRange(headers);

        foreach (var row in range.Rows().Skip(1))
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var isEmpty = true;
            for (var i = 0; i < headers.Count; i++)
            {
                var value = row.Cell(i + 1).GetFormattedString().Trim();
                if (value.Length > 0) isEmpty = false;
                dict[headers[i]] = value;
            }
            if (!isEmpty) result.Rows.Add(dict);
        }
        return result;
    }

    private static string Normalize(string header)
        => new string(header.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

    private static string? DetectEmailColumn(ParsedRecipientFile file)
    {
        foreach (var candidate in EmailHeaderCandidates)
        {
            var match = file.Columns.FirstOrDefault(c => Normalize(c) == Normalize(candidate));
            if (match is not null) return match;
        }

        var contains = file.Columns.FirstOrDefault(c => Normalize(c).Contains("mail"));
        if (contains is not null) return contains;

        // Fall back to the column where most values look like email addresses.
        if (file.Rows.Count == 0) return null;
        var sample = file.Rows.Take(50).ToList();
        string? best = null;
        var bestScore = 0;
        foreach (var column in file.Columns)
        {
            var score = sample.Count(r => r.TryGetValue(column, out var v) && IsValidEmail(v));
            if (score > bestScore)
            {
                bestScore = score;
                best = column;
            }
        }
        return bestScore > 0 ? best : null;
    }

    private static string? DetectNameColumn(List<string> columns)
    {
        foreach (var candidate in NameHeaderCandidates)
        {
            var match = columns.FirstOrDefault(c => Normalize(c) == Normalize(candidate));
            if (match is not null) return match;
        }
        return columns.FirstOrDefault(c => Normalize(c).Contains("name"));
    }
}
