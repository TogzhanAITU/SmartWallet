using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace Budget_Summary
{
    public record EnrichedExpense(
        string Id,
        decimal Amount,
        string Category,
        bool IsMandatory,
        int TaxCents,
        int Score);

    public record CategorySummaryRow(
        string Category, int ExpenseCount, decimal TotalAmount, int TotalTaxCents);

    public record CategorySummaryReport(
        IReadOnlyList<CategorySummaryRow> Rows, decimal GrandTotal);

    public record PriorityRow(
        string Id, string Category, decimal Amount, int TaxCents, int Score);

    public record PriorityReport(IReadOnlyList<PriorityRow> Rows);

    public record ScoreConfig(
        int AmountWeight, int MandatoryBonus, int TaxPenaltyWeight);

    public static class Scoring
    {
        public static Func<ScoreConfig, Func<EnrichedExpense, int>> CreateScore() =>
            config => expense =>
                (int)(expense.Amount * config.AmountWeight)
                + (expense.IsMandatory ? config.MandatoryBonus : 0)
                - (expense.TaxCents / 100) * config.TaxPenaltyWeight;
    }

    public static class BudgetExtensions
    {
        public static Result<IReadOnlyList<ExpenseRecord>, DomainError> ValidateForReporting(
            this IEnumerable<ExpenseRecord> expenses)
        {
            if (expenses is null)
                return Result<IReadOnlyList<ExpenseRecord>, DomainError>.Failure(
                    new DomainError("NULL_EXPENSES", "Expense collection is null."));

            var snapshot = expenses.ToList();
            foreach (var e in snapshot)
            {
                if (e is null)
                    return Result<IReadOnlyList<ExpenseRecord>, DomainError>.Failure(
                        new DomainError("NULL_EXPENSE", "Collection contains a null expense."));
                if (string.IsNullOrWhiteSpace(e.Id))
                    return Result<IReadOnlyList<ExpenseRecord>, DomainError>.Failure(
                        new DomainError("INVALID_ID", "Expense id must not be empty."));
                if (e.Amount <= 0)
                    return Result<IReadOnlyList<ExpenseRecord>, DomainError>.Failure(
                        new DomainError("INVALID_AMOUNT",
                            $"Expense {e.Id} amount must be greater than zero."));
                if (string.IsNullOrWhiteSpace(e.Category))
                    return Result<IReadOnlyList<ExpenseRecord>, DomainError>.Failure(
                        new DomainError("INVALID_CATEGORY",
                            $"Expense {e.Id} category must not be empty."));
            }
            return Result<IReadOnlyList<ExpenseRecord>, DomainError>.Success(snapshot);
        }

        public static Result<EnrichedExpense, DomainError> EnrichWithTax(
            this ExpenseRecord expense, ITaxFeeAdapter adapter, int taxBps)
        {
            if (expense is null)
                return Result<EnrichedExpense, DomainError>.Failure(
                    new DomainError("NULL_EXPENSE", "Expense is null."));
            if (adapter is null)
                return Result<EnrichedExpense, DomainError>.Failure(
                    new DomainError("NULL_ADAPTER", "Tax adapter is null."));
            if (expense.Amount <= 0)
                return Result<EnrichedExpense, DomainError>.Failure(
                    new DomainError("INVALID_AMOUNT",
                        $"Expense {expense.Id} amount must be greater than zero."));

            return adapter.ComputeTaxCents(expense.Amount, taxBps).Match(
                taxCents => Result<EnrichedExpense, DomainError>.Success(
                    new EnrichedExpense(expense.Id, expense.Amount, expense.Category,
                        expense.IsMandatory, taxCents, 0)),
                error => Result<EnrichedExpense, DomainError>.Failure(error));
        }

        public static IEnumerable<EnrichedExpense> WhereCategory(
            this IEnumerable<EnrichedExpense> expenses, string category) =>
            expenses.Where(e => e.Category.Equals(category, StringComparison.OrdinalIgnoreCase));

        public static decimal SumMandatory(this IEnumerable<EnrichedExpense> expenses) =>
            expenses.Where(e => e.IsMandatory).Sum(e => e.Amount);
    }

    public static class BudgetWorkflowRunner
    {
        public static Result<IReadOnlyList<EnrichedExpense>, DomainError> EnrichExpenses(
            IEnumerable<ExpenseRecord> expenses,
            ITaxFeeAdapter adapter,
            int taxBps,
            Func<EnrichedExpense, int> configuredScore)
        {
            var validated = expenses.ValidateForReporting();
            if (!validated.IsSuccess)
                return Result<IReadOnlyList<EnrichedExpense>, DomainError>.Failure(validated.Error);

            var enriched = new List<EnrichedExpense>();
            foreach (var e in validated.Value)
            {
                var tax = e.EnrichWithTax(adapter, taxBps);
                if (!tax.IsSuccess)
                    return Result<IReadOnlyList<EnrichedExpense>, DomainError>.Failure(tax.Error);

                enriched.Add(tax.Value with { Score = configuredScore(tax.Value) });
            }
            return Result<IReadOnlyList<EnrichedExpense>, DomainError>.Success(enriched);
        }
    }

    public static class BudgetReports
    {
        private static Result<IReadOnlyList<EnrichedExpense>, DomainError> Snapshot(
            IEnumerable<EnrichedExpense> expenses)
        {
            if (expenses is null)
                return Result<IReadOnlyList<EnrichedExpense>, DomainError>.Failure(
                    new DomainError("NULL_EXPENSES", "Collection is null."));

            var snapshot = expenses.ToList();
            foreach (var e in snapshot)
            {
                if (e is null)
                    return Result<IReadOnlyList<EnrichedExpense>, DomainError>.Failure(
                        new DomainError("NULL_EXPENSE", "Enriched collection contains null."));
                if (string.IsNullOrWhiteSpace(e.Id) || string.IsNullOrWhiteSpace(e.Category))
                    return Result<IReadOnlyList<EnrichedExpense>, DomainError>.Failure(
                        new DomainError("INVALID_STRING", "Id/Category must not be empty."));
                if (e.Amount <= 0)
                    return Result<IReadOnlyList<EnrichedExpense>, DomainError>.Failure(
                        new DomainError("INVALID_AMOUNT", $"Expense {e.Id} amount must be > 0."));
                if (e.TaxCents < 0)
                    return Result<IReadOnlyList<EnrichedExpense>, DomainError>.Failure(
                        new DomainError("INVALID_TAX", $"Expense {e.Id} native tax must not be negative."));
            }
            return Result<IReadOnlyList<EnrichedExpense>, DomainError>.Success(snapshot);
        }

        public static Result<CategorySummaryReport, DomainError> BuildCategorySummary(
            IEnumerable<EnrichedExpense> expenses)
        {
            var snapshot = Snapshot(expenses);
            if (!snapshot.IsSuccess)
                return Result<CategorySummaryReport, DomainError>.Failure(snapshot.Error);

            var rows = snapshot.Value
                .GroupBy(e => e.Category, StringComparer.Ordinal)
                .Select(g => new CategorySummaryRow(
                    g.Key, g.Count(), g.Sum(e => e.Amount), g.Sum(e => e.TaxCents)))
                .OrderByDescending(r => r.TotalAmount)
                .ThenBy(r => r.Category, StringComparer.Ordinal)
                .ToList();

            return Result<CategorySummaryReport, DomainError>.Success(
                new CategorySummaryReport(rows, rows.Sum(r => r.TotalAmount)));
        }

        public static Result<PriorityReport, DomainError> BuildPriorityReport(
            IEnumerable<EnrichedExpense> expenses, int minScore)
        {
            var snapshot = Snapshot(expenses);
            if (!snapshot.IsSuccess)
                return Result<PriorityReport, DomainError>.Failure(snapshot.Error);

            var rows = snapshot.Value
                .Where(e => e.Score >= minScore)
                .Select(e => new PriorityRow(e.Id, e.Category, e.Amount, e.TaxCents, e.Score))
                .OrderByDescending(r => r.Score)
                .ThenByDescending(r => r.Amount)
                .ThenBy(r => r.Id, StringComparer.Ordinal)
                .ToList();

            return Result<PriorityReport, DomainError>.Success(new PriorityReport(rows));
        }

        public static IEnumerable<PriorityRow> EnumeratePriorityRows(
            IEnumerable<EnrichedExpense> expenses, int minScore)
        {
            if (expenses is null) yield break;
            foreach (var e in expenses)
                if (e.Score >= minScore)
                    yield return new PriorityRow(e.Id, e.Category, e.Amount, e.TaxCents, e.Score);
        }
    }

    public static class ReportExporter
    {
        public static Result<string, DomainError> ExportCategorySummaryCsv(
            string path, CategorySummaryReport report)
        {
            if (string.IsNullOrWhiteSpace(path))
                return Result<string, DomainError>.Failure(
                    new DomainError("INVALID_PATH", "Export path must not be empty."));
            if (report is null)
                return Result<string, DomainError>.Failure(
                    new DomainError("NULL_REPORT", "Report is null."));

            try
            {
                using var writer = new StreamWriter(path);
                writer.WriteLine("Category,ExpenseCount,TotalAmount,TotalTaxCents");
                foreach (var row in report.Rows)
                {
                    writer.WriteLine(
                        $"{Escape(row.Category)},{row.ExpenseCount}," +
                        $"{row.TotalAmount.ToString(CultureInfo.InvariantCulture)},{row.TotalTaxCents}");
                }
                return Result<string, DomainError>.Success(path);
            }
            catch (Exception ex)
            {
                return Result<string, DomainError>.Failure(
                    new DomainError("EXPORT_FAILED", ex.Message));
            }
        }

        private static string Escape(string v) => "\"" + v.Replace("\"", "\"\"") + "\"";
    }
}