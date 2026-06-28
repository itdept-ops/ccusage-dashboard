using System.Globalization;

namespace Ccusage.Api.Services;

/// <summary>
/// Dependency-free parsers for the TWO generic import formats that sit alongside the Rocket Money path:
/// a column-mapped GENERIC BANK CSV (the user tells us which columns are date/amount/description/category/
/// account, with a DEBIT/CREDIT column pair + a sign/negate toggle since banks differ) and a hand-rolled
/// OFX/QFX statement parser (<c>&lt;STMTTRN&gt;…&lt;DTPOSTED&gt;…&lt;TRNAMT&gt;…&lt;NAME&gt;…&lt;FITID&gt;</c>).
/// Both REUSE <see cref="RocketMoneyCsv"/>'s field helpers (the CSV reader, date/amount parsing, classification)
/// so every format behaves consistently. Each produces the SAME <see cref="ParsedTxn"/> rows the staging
/// pipeline consumes; FITID (when present) is carried through as the BEST dedup key.
/// </summary>
public static class BankImportParsers
{
    /// <summary>One parsed transaction from any generic format — the staging pipeline's common shape. Carries
    /// the optional bank-supplied <see cref="Fitid"/> (the preferred dedup key for OFX).</summary>
    public sealed record ParsedTxn(
        int RowIndex,
        string AccountName,
        string Institution,
        string AccountTypeRaw,
        DateOnly Date,
        string Merchant,
        string? Description,
        decimal RawAmount,
        decimal Magnitude,
        RocketMoneyCsv.ParsedKind Kind,
        string? Category,
        string? Fitid);

    /// <summary>The outcome of parsing a file: the rows + how many were seen vs skipped.</summary>
    public sealed record ParseResult(IReadOnlyList<ParsedTxn> Rows, int RowCount, int SkippedCount,
        IReadOnlyList<string> DetectedColumns);

    /// <summary>
    /// The caller-supplied column map for a GENERIC bank CSV. Each value is a HEADER NAME (case-insensitive)
    /// in the uploaded file, except the optional account-name/institution literals.
    /// <list type="bullet">
    ///   <item><see cref="Date"/>, <see cref="Description"/>, <see cref="Category"/>: header names.</item>
    ///   <item><see cref="Amount"/>: a single signed-amount column — OR leave it null and supply
    ///   <see cref="Debit"/>/<see cref="Credit"/> for banks that split money-out/money-in into two columns.</item>
    ///   <item><see cref="Negate"/>: flip the sign of the parsed amount (some banks write expenses as positive).</item>
    ///   <item><see cref="Account"/>: a header naming the account per-row — OR <see cref="AccountName"/>/
    ///   <see cref="Institution"/> literals applied to every row when the file has no account column.</item>
    /// </list>
    /// </summary>
    public sealed record ColumnMap(
        string? Date = null,
        string? Amount = null,
        string? Debit = null,
        string? Credit = null,
        bool Negate = false,
        string? Description = null,
        string? Category = null,
        string? Account = null,
        string? AccountName = null,
        string? Institution = null);

    // ===================================================================================
    // GENERIC bank CSV (user-mapped columns; debit/credit pair; sign/negate toggle)
    // ===================================================================================

    /// <summary>
    /// Parse a generic bank CSV using the caller's <paramref name="map"/>. Unmapped/unparseable rows are
    /// skipped + counted (never aborting the file). Amount resolution: if <see cref="ColumnMap.Amount"/> is
    /// mapped it's the signed amount; otherwise a value in the <see cref="ColumnMap.Debit"/> column is money OUT
    /// (negative) and a value in <see cref="ColumnMap.Credit"/> is money IN (positive). The
    /// <see cref="ColumnMap.Negate"/> toggle flips the final sign. Account name/institution come from the
    /// per-row account column when mapped, else the map's literals, else "Imported account".
    /// </summary>
    public static ParseResult ParseGenericCsv(string content, ColumnMap map)
    {
        var records = RocketMoneyCsv.ReadRecordsShared(content);
        if (records.Count == 0)
            return new ParseResult(Array.Empty<ParsedTxn>(), 0, 0, Array.Empty<string>());

        var header = records[0];
        var detected = header.Select(h => h.Trim()).Where(h => h.Length > 0).ToList();
        var col = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < header.Count; i++)
        {
            var key = header[i].Trim();
            if (key.Length > 0 && !col.ContainsKey(key)) col[key] = i;
        }

        int? Idx(string? name) =>
            !string.IsNullOrWhiteSpace(name) && col.TryGetValue(name.Trim(), out var i) ? i : (int?)null;

        var dateIdx = Idx(map.Date);
        var amountIdx = Idx(map.Amount);
        var debitIdx = Idx(map.Debit);
        var creditIdx = Idx(map.Credit);
        var descIdx = Idx(map.Description);
        var catIdx = Idx(map.Category);
        var acctIdx = Idx(map.Account);

        var literalAccount = RocketMoneyCsv.ClampShared(map.AccountName, 200);
        var literalInstitution = RocketMoneyCsv.ClampShared(map.Institution, 200);

        var rows = new List<ParsedTxn>();
        var skipped = 0;
        var dataCount = 0;
        var rowIndex = 0;

        for (var r = 1; r < records.Count; r++)
        {
            var fields = records[r];
            if (fields.Count == 0 || (fields.Count == 1 && string.IsNullOrWhiteSpace(fields[0])))
                continue;

            var thisIndex = rowIndex++;
            dataCount++;

            var dateStr = At(fields, dateIdx);
            if (!RocketMoneyCsv.TryParseDateShared(dateStr, out var date))
            {
                skipped++;
                continue;
            }

            if (!TryResolveAmount(fields, amountIdx, debitIdx, creditIdx, map.Negate, out var raw))
            {
                skipped++;
                continue;
            }

            var description = RocketMoneyCsv.NullIfBlankShared(At(fields, descIdx));
            // Merchant: the description column doubles as the merchant when no dedicated name column exists.
            var merchant = description ?? "";
            var category = RocketMoneyCsv.NullIfBlankShared(At(fields, catIdx));

            var accountName = acctIdx is int ai
                ? RocketMoneyCsv.ClampShared(At(fields, ai), 200)
                : literalAccount;
            if (string.IsNullOrWhiteSpace(accountName)) accountName = "Imported account";
            var institution = literalInstitution;

            var kind = RocketMoneyCsv.ClassifyRow(category, raw, ""); // no account-type token in a generic CSV
            rows.Add(new ParsedTxn(
                thisIndex, accountName, institution, "", date,
                RocketMoneyCsv.ClampShared(merchant, 300),
                description is null ? null : RocketMoneyCsv.ClampShared(description, 500),
                raw, Math.Abs(raw), kind,
                category is null ? null : RocketMoneyCsv.ClampShared(category, 120),
                Fitid: null));
        }

        return new ParseResult(rows, dataCount, skipped, detected);
    }

    /// <summary>Resolve the row's signed amount from either a single signed column or a debit/credit pair,
    /// then apply the negate toggle. False when nothing usable is present.</summary>
    private static bool TryResolveAmount(
        List<string> fields, int? amountIdx, int? debitIdx, int? creditIdx, bool negate, out decimal amount)
    {
        amount = 0m;

        if (amountIdx is int ai)
        {
            if (!RocketMoneyCsv.TryParseAmountShared(At(fields, ai), out amount)) return false;
        }
        else if (debitIdx is not null || creditIdx is not null)
        {
            // A debit is money OUT (negative); a credit is money IN (positive). Use magnitudes so a bank that
            // writes its debit column as a positive number still becomes money-out.
            decimal d = 0m, c = 0m;
            var hasDebit = debitIdx is int di
                && RocketMoneyCsv.TryParseAmountShared(At(fields, di), out d) && d != 0m;
            var hasCredit = creditIdx is int ci
                && RocketMoneyCsv.TryParseAmountShared(At(fields, ci), out c) && c != 0m;

            if (!hasDebit && !hasCredit) return false;
            amount = Math.Abs(c) - Math.Abs(d);
        }
        else
        {
            return false; // neither an amount column nor a debit/credit pair was mapped
        }

        if (negate) amount = -amount;
        return true;
    }

    private static string At(List<string> fields, int? idx) =>
        idx is int i && i >= 0 && i < fields.Count ? fields[i] : "";

    // ===================================================================================
    // OFX / QFX (hand-rolled, dependency-free; FITID is the preferred dedup key)
    // ===================================================================================

    /// <summary>
    /// Parse an OFX/QFX statement. OFX is SGML-ish: tags like <c>&lt;TRNAMT&gt;12.34</c> often have no closing
    /// tag (the value runs to the next tag or newline), so this reads each <c>&lt;STMTTRN&gt;…&lt;/STMTTRN&gt;</c>
    /// block and pulls <c>&lt;DTPOSTED&gt;</c> (YYYYMMDD[hhmmss]), <c>&lt;TRNAMT&gt;</c>, <c>&lt;NAME&gt;</c>
    /// (falling back to <c>&lt;MEMO&gt;</c>), <c>&lt;MEMO&gt;</c>, <c>&lt;TRNTYPE&gt;</c>, and <c>&lt;FITID&gt;</c>.
    /// Account context comes from <c>&lt;ACCTID&gt;</c>/<c>&lt;BANKID&gt;</c>/<c>&lt;ACCTTYPE&gt;</c> +
    /// <c>&lt;ORG&gt;</c>; a credit-card statement (<c>&lt;CCSTMTRS&gt;</c>) is typed "credit". Unparseable
    /// blocks are skipped + counted. FITID is returned as the bank's stable txn id.
    /// </summary>
    public static ParseResult ParseOfx(string content)
    {
        content ??= "";

        // Institution org (optional) and per-statement account context.
        var org = ExtractTag(content, "ORG");

        var rows = new List<ParsedTxn>();
        var skipped = 0;
        var seen = 0;
        var rowIndex = 0;

        // Each <STMTRS> / <CCSTMTRS> block carries one account's context + its <BANKTRANLIST>. Walk all
        // statement blocks so a multi-account OFX maps to multiple accounts.
        foreach (var stmt in StatementBlocks(content))
        {
            var acctId = ExtractTag(stmt.Body, "ACCTID");
            var bankId = ExtractTag(stmt.Body, "BANKID");
            var acctType = stmt.IsCredit ? "credit" : ExtractTag(stmt.Body, "ACCTTYPE");

            var accountName = !string.IsNullOrWhiteSpace(acctId)
                ? (stmt.IsCredit ? $"Card {Last4(acctId)}" : $"Account {Last4(acctId)}")
                : (stmt.IsCredit ? "Credit card" : "Imported account");
            var institution = RocketMoneyCsv.ClampShared(
                !string.IsNullOrWhiteSpace(org) ? org : bankId, 200);

            foreach (var block in TransactionBlocks(stmt.Body))
            {
                var thisIndex = rowIndex++;
                seen++;

                var dateStr = ExtractTag(block, "DTPOSTED");
                var amountStr = ExtractTag(block, "TRNAMT");
                if (!TryParseOfxDate(dateStr, out var date)
                    || !RocketMoneyCsv.TryParseAmountShared(amountStr, out var raw))
                {
                    skipped++;
                    continue;
                }

                var name = ExtractTag(block, "NAME");
                var memo = ExtractTag(block, "MEMO");
                var merchant = !string.IsNullOrWhiteSpace(name) ? name : memo;
                if (string.IsNullOrWhiteSpace(merchant)) merchant = "(no description)";
                var description = RocketMoneyCsv.NullIfBlankShared(memo);
                var fitid = RocketMoneyCsv.NullIfBlankShared(ExtractTag(block, "FITID"));

                var kind = RocketMoneyCsv.ClassifyRow(null, raw, acctType);
                rows.Add(new ParsedTxn(
                    thisIndex,
                    RocketMoneyCsv.ClampShared(accountName, 200),
                    institution,
                    RocketMoneyCsv.ClampShared(acctType, 120),
                    date,
                    RocketMoneyCsv.ClampShared(merchant, 300),
                    description is null ? null : RocketMoneyCsv.ClampShared(description, 500),
                    raw, Math.Abs(raw), kind,
                    Category: null,
                    Fitid: fitid is null ? null : RocketMoneyCsv.ClampShared(fitid, 255)));
            }
        }

        return new ParseResult(rows, seen, skipped, Array.Empty<string>());
    }

    private readonly record struct OfxStatement(string Body, bool IsCredit);

    /// <summary>Yield each statement block (<c>&lt;STMTRS&gt;</c> bank, <c>&lt;CCSTMTRS&gt;</c> credit card).
    /// If none are delimited, treat the whole document as one bank statement.</summary>
    private static IEnumerable<OfxStatement> StatementBlocks(string content)
    {
        var any = false;
        foreach (var (tag, isCredit) in new[] { ("STMTRS", false), ("CCSTMTRS", true) })
        {
            foreach (var body in BlocksByTag(content, tag))
            {
                any = true;
                yield return new OfxStatement(body, isCredit);
            }
        }
        if (!any) yield return new OfxStatement(content, false);
    }

    /// <summary>Yield each <c>&lt;STMTTRN&gt;…&lt;/STMTTRN&gt;</c> transaction block's inner text.</summary>
    private static IEnumerable<string> TransactionBlocks(string content) => BlocksByTag(content, "STMTTRN");

    /// <summary>Yield the inner text of every <c>&lt;TAG&gt;…&lt;/TAG&gt;</c> aggregate (case-insensitive).</summary>
    private static IEnumerable<string> BlocksByTag(string content, string tag)
    {
        var open = "<" + tag + ">";
        var close = "</" + tag + ">";
        var pos = 0;
        while (true)
        {
            var start = content.IndexOf(open, pos, StringComparison.OrdinalIgnoreCase);
            if (start < 0) yield break;
            var bodyStart = start + open.Length;
            var end = content.IndexOf(close, bodyStart, StringComparison.OrdinalIgnoreCase);
            if (end < 0) yield break;
            yield return content.Substring(bodyStart, end - bodyStart);
            pos = end + close.Length;
        }
    }

    /// <summary>
    /// Read a single OFX element's value. OFX rarely closes scalar tags, so the value runs from just after
    /// <c>&lt;TAG&gt;</c> to the next <c>&lt;</c> (or end). Honors an explicit closing tag if present. Returns
    /// the trimmed value, or "" when absent.
    /// </summary>
    private static string ExtractTag(string content, string tag)
    {
        var open = "<" + tag + ">";
        var i = content.IndexOf(open, StringComparison.OrdinalIgnoreCase);
        if (i < 0) return "";
        var valueStart = i + open.Length;
        var end = valueStart;
        while (end < content.Length && content[end] != '<') end++;
        return DecodeEntities(content.Substring(valueStart, end - valueStart).Trim());
    }

    /// <summary>Parse an OFX DTPOSTED: YYYYMMDD optionally followed by HHMMSS[.xxx] and a [tz] suffix.</summary>
    private static bool TryParseOfxDate(string? s, out DateOnly date)
    {
        date = default;
        s = (s ?? "").Trim();
        if (s.Length < 8) return false;
        var ymd = s[..8];
        if (DateOnly.TryParseExact(ymd, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
            return true;
        // Last resort: let the shared parser try (handles odd but valid formats).
        return RocketMoneyCsv.TryParseDateShared(s, out date);
    }

    /// <summary>The last 4 chars of an account id (for a friendly masked account name).</summary>
    private static string Last4(string acctId)
    {
        var t = acctId.Trim();
        return t.Length <= 4 ? t : t[^4..];
    }

    /// <summary>Decode the handful of XML/SGML entities OFX uses in NAME/MEMO values.</summary>
    private static string DecodeEntities(string s) =>
        s.Replace("&amp;", "&").Replace("&lt;", "<").Replace("&gt;", ">")
         .Replace("&apos;", "'").Replace("&quot;", "\"");
}
