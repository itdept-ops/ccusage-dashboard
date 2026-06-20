using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Ccusage.Api.Services;

/// <summary>
/// A small, dependency-free parser for the Rocket Money transaction export. Rocket Money aggregates ALL of
/// a user's linked accounts into one CSV, so a single file holds rows from several distinct accounts; the
/// importer groups them by (Account Name, Institution Name).
///
/// <para>The CSV reader is RFC4180-ish: it handles double-quoted fields that contain commas and embedded
/// newlines, and the <c>""</c> escape for a literal quote inside a quoted field. Columns are mapped by
/// HEADER NAME (case-insensitive), so extra/missing/re-ordered columns are tolerated. Typical header:</para>
///
/// <code>Date, Original Date, Account Type, Account Name, Institution Name, Name, Custom Name, Amount,
/// Description, Category, Note, Ignored From, Tax Deductible</code>
///
/// <para>Per-field rules: Date parses common formats (yyyy-MM-dd, M/d/yyyy, …); Amount strips a leading
/// "$" and thousands commas and reads parentheses OR a leading minus as negative; Merchant = Custom Name
/// (falling back to Name); Category comes straight from Category. Unparseable rows (no usable date or
/// amount) are skipped and counted, never aborting the whole import.</para>
///
/// <para>Each row is classified into a <see cref="ParsedKind"/>: <c>income</c> (an income-ish category, or a
/// positive amount landing in a bank account), <c>transfer</c> (a transfer / credit-card-payment / payment
/// category), else <c>expense</c>. The dashboard's spending math uses EXPENSE only.</para>
/// </summary>
public static class RocketMoneyCsv
{
    public enum ParsedKind { Expense, Income, Transfer }

    /// <summary>One successfully-parsed transaction row (before persistence/dedup).</summary>
    public sealed record ParsedRow(
        string AccountName,
        string Institution,
        string AccountTypeRaw,
        DateOnly Date,
        string Merchant,
        string? Description,
        decimal RawAmount,
        decimal Magnitude,
        ParsedKind Kind,
        string? Category,
        string? Note);

    /// <summary>The outcome of parsing a whole file: the parsed rows plus how many were seen vs skipped.</summary>
    public sealed record ParseResult(IReadOnlyList<ParsedRow> Rows, int RowCount, int SkippedCount);

    // Categories that mark a row as INCOME even if it isn't obviously a bank credit.
    private static readonly HashSet<string> IncomeCategories = new(StringComparer.OrdinalIgnoreCase)
    {
        "income", "paycheck", "paychecks", "interest", "interest income", "dividend", "dividends",
        "wages", "salary", "bonus", "refund", "refunds", "reimbursement", "tax refund",
    };

    // Categories that mark a row as a TRANSFER (excluded from spending). Credit-card payments and generic
    // "payment"/"transfer" categories move money between the household's own accounts.
    private static readonly HashSet<string> TransferCategories = new(StringComparer.OrdinalIgnoreCase)
    {
        "transfer", "transfers", "credit card payment", "credit card payments", "payment", "payments",
        "balance transfer", "internal transfer", "account transfer",
    };

    /// <summary>Parse a Rocket Money CSV. Tolerant: unparseable rows are skipped and counted.</summary>
    public static ParseResult Parse(string content)
    {
        var records = ReadRecords(content);
        if (records.Count == 0)
            return new ParseResult(Array.Empty<ParsedRow>(), 0, 0);

        // First non-empty record is the header. Map column name -> index (case-insensitively).
        var header = records[0];
        var col = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < header.Count; i++)
        {
            var key = header[i].Trim();
            if (key.Length > 0 && !col.ContainsKey(key)) col[key] = i;
        }

        var rows = new List<ParsedRow>();
        var skipped = 0;
        var dataCount = 0;

        for (var r = 1; r < records.Count; r++)
        {
            var fields = records[r];
            // A wholly blank line (single empty field) is not a data row — ignore without counting.
            if (fields.Count == 0 || (fields.Count == 1 && string.IsNullOrWhiteSpace(fields[0])))
                continue;

            dataCount++;

            var dateStr = Get(fields, col, "Date");
            if (string.IsNullOrWhiteSpace(dateStr)) dateStr = Get(fields, col, "Original Date");
            var amountStr = Get(fields, col, "Amount");

            if (!TryParseDate(dateStr, out var date) || !TryParseAmount(amountStr, out var raw))
            {
                skipped++;
                continue;
            }

            var accountName = Get(fields, col, "Account Name").Trim();
            var institution = Get(fields, col, "Institution Name").Trim();
            var accountType = Get(fields, col, "Account Type").Trim();

            var customName = Get(fields, col, "Custom Name").Trim();
            var name = Get(fields, col, "Name").Trim();
            var merchant = customName.Length > 0 ? customName : name;

            var description = NullIfBlank(Get(fields, col, "Description"));
            var category = NullIfBlank(Get(fields, col, "Category"));
            var note = NullIfBlank(Get(fields, col, "Note"));

            var kind = Classify(category, raw, accountType);
            var magnitude = Math.Abs(raw);

            rows.Add(new ParsedRow(
                accountName, institution, accountType, date,
                Clamp(merchant, 300), description is null ? null : Clamp(description, 500),
                raw, magnitude, kind, category is null ? null : Clamp(category, 120),
                note is null ? null : Clamp(note, 1000)));
        }

        return new ParseResult(rows, dataCount, skipped);
    }

    /// <summary>Infer an account's <see cref="Data.Entities.FinanceAccount.Kind"/> from the CSV Account Type.</summary>
    public static string AccountKind(string accountTypeRaw)
    {
        var t = (accountTypeRaw ?? "").Trim().ToLowerInvariant();
        if (t.Contains("credit")) return "credit";
        if (t.Contains("checking") || t.Contains("savings") || t.Contains("bank")
            || t.Contains("depository") || t.Contains("cash")) return "bank";
        return "other";
    }

    /// <summary>
    /// The stable dedup hash over (accountKey | date | amount | merchant | description). Re-importing the
    /// same export produces identical hashes, so the UNIQUE (HouseholdId, DedupHash) index skips duplicates.
    /// </summary>
    public static string DedupHash(string accountKey, DateOnly date, decimal rawAmount, string merchant, string? description)
    {
        var canonical = string.Join('|',
            accountKey.Trim().ToLowerInvariant(),
            date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            rawAmount.ToString("0.00", CultureInfo.InvariantCulture),
            (merchant ?? "").Trim().ToLowerInvariant(),
            (description ?? "").Trim().ToLowerInvariant());
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexString(bytes); // 64 hex chars
    }

    /// <summary>The stable key identifying an account within a file: "name|institution" (lower-cased).</summary>
    public static string AccountKey(string accountName, string institution) =>
        $"{(accountName ?? "").Trim().ToLowerInvariant()}|{(institution ?? "").Trim().ToLowerInvariant()}";

    // ---- classification ----

    private static ParsedKind Classify(string? category, decimal raw, string accountType)
    {
        var cat = (category ?? "").Trim();
        if (cat.Length > 0)
        {
            if (TransferCategories.Contains(cat)) return ParsedKind.Transfer;
            if (IncomeCategories.Contains(cat)) return ParsedKind.Income;
        }

        // No income/transfer category: a positive (credit) amount landing in a BANK account is income
        // (e.g. a deposit/paycheck with no category). On a credit card, a positive amount is typically a
        // payment/refund — treat as a transfer so it never counts as "spending".
        if (raw > 0)
            return AccountKind(accountType) == "bank" ? ParsedKind.Income : ParsedKind.Transfer;

        return ParsedKind.Expense;
    }

    // ---- field parsing ----

    private static bool TryParseDate(string? s, out DateOnly date)
    {
        date = default;
        s = (s ?? "").Trim();
        if (s.Length == 0) return false;

        // Rocket Money commonly emits yyyy-MM-dd; tolerate US M/d/yyyy and a few near variants.
        string[] formats =
        {
            "yyyy-MM-dd", "yyyy/MM/dd", "M/d/yyyy", "MM/dd/yyyy", "M/d/yy", "MM/dd/yy",
            "M-d-yyyy", "MM-dd-yyyy", "d/M/yyyy",
        };
        if (DateOnly.TryParseExact(s, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
            return true;
        // Last resort: a culture-invariant general parse (handles trailing time components etc.).
        if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
        {
            date = DateOnly.FromDateTime(dt);
            return true;
        }
        return false;
    }

    private static bool TryParseAmount(string? s, out decimal amount)
    {
        amount = 0m;
        s = (s ?? "").Trim();
        if (s.Length == 0) return false;

        var negative = false;
        // Parentheses denote a negative amount: (12.34) => -12.34.
        if (s.StartsWith('(') && s.EndsWith(')'))
        {
            negative = true;
            s = s[1..^1].Trim();
        }
        // A leading minus (possibly before the $) is also negative.
        if (s.StartsWith('-'))
        {
            negative = true;
            s = s[1..].Trim();
        }
        // Strip currency symbol and thousands separators.
        s = s.Replace("$", "").Replace(",", "").Replace("+", "").Trim();
        if (s.StartsWith('-')) { negative = true; s = s[1..].Trim(); }
        if (s.Length == 0) return false;

        if (!decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out amount))
            return false;

        if (negative) amount = -amount;
        return true;
    }

    // ---- RFC4180-ish record reader (quoted fields with commas/newlines + "" escape) ----

    /// <summary>
    /// Split the whole CSV text into records, each a list of fields. Honors double-quoted fields containing
    /// commas and newlines, and the <c>""</c> escape for an embedded quote. Tolerates both \r\n and \n line
    /// endings. Trailing blank lines are dropped.
    /// </summary>
    private static List<List<string>> ReadRecords(string content)
    {
        var records = new List<List<string>>();
        if (string.IsNullOrEmpty(content)) return records;

        var field = new StringBuilder();
        var current = new List<string>();
        var inQuotes = false;
        var fieldStarted = false; // did we begin any field on this record (to distinguish a real empty row)

        void EndField()
        {
            current.Add(field.ToString());
            field.Clear();
        }
        void EndRecord()
        {
            EndField();
            records.Add(current);
            current = new List<string>();
            fieldStarted = false;
        }

        for (var i = 0; i < content.Length; i++)
        {
            var c = content[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    // Lookahead for an escaped quote ("").
                    if (i + 1 < content.Length && content[i + 1] == '"')
                    {
                        field.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    field.Append(c);
                }
                continue;
            }

            switch (c)
            {
                case '"':
                    inQuotes = true;
                    fieldStarted = true;
                    break;
                case ',':
                    fieldStarted = true;
                    EndField();
                    break;
                case '\r':
                    // swallow; the \n (or end) finishes the record
                    if (i + 1 < content.Length && content[i + 1] == '\n') { /* handled on \n */ }
                    else { EndRecord(); }
                    break;
                case '\n':
                    EndRecord();
                    break;
                default:
                    fieldStarted = true;
                    field.Append(c);
                    break;
            }
        }

        // Flush a trailing field/record if the file didn't end with a newline.
        if (field.Length > 0 || current.Count > 0 || fieldStarted)
            EndRecord();

        // Drop fully-empty trailing records (a single empty field with nothing else).
        while (records.Count > 0)
        {
            var last = records[^1];
            if (last.Count == 1 && last[0].Length == 0) records.RemoveAt(records.Count - 1);
            else break;
        }

        return records;
    }

    // ---- small helpers ----

    private static string Get(List<string> fields, Dictionary<string, int> col, string name) =>
        col.TryGetValue(name, out var idx) && idx < fields.Count ? fields[idx] : "";

    private static string? NullIfBlank(string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static string Clamp(string s, int max)
    {
        s = (s ?? "").Trim();
        return s.Length > max ? s[..max] : s;
    }
}
