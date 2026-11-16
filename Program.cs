using System;
using System.IO;
using System.Linq;

namespace Budget_Summary
{
    public class Program
    {
        public static void Main(string[] args)
        {
            RunPureWorkflowDemo();
            RunIntegratedReportsAndNative();
        }

        private static void RunPureWorkflowDemo()
        {
            Console.WriteLine("=== 1. Normal workflow ===");
            var draft = BudgetWorkflow.Start(1000m, isMandatory: true);
            var validated = BudgetWorkflow.Validate(draft);
            var taxed = BudgetWorkflow.Bind(validated,
                v => BudgetWorkflow.ApplyTaxPolicy(v, 0.10m, 1.5m));

            Func<decimal, string> gateway = amount =>
            {
                string txId = "TXN-" + Guid.NewGuid().ToString()[..8].ToUpper();
                Console.WriteLine($"[Gateway] Charging ${amount:F2}... Approved. ID: {txId}");
                return txId;
            };

            var receipt = BudgetWorkflow.Bind(taxed, t => BudgetWorkflow.FinalizeOnce(t, gateway));
            receipt.Handle(
                r => Console.WriteLine(
                    $"[Success] Receipt {r.TransactionId}: ${r.Amount:F2} (tax ${r.TaxAmount:F2})\n"),
                e => Console.WriteLine($"[Failure] [{e.Code}] {e.Message}\n"));

            Console.WriteLine("=== 2. Double-finalize rejected ===");
            var taxedItem = new TaxedExpense(500m, 50m);
            var first  = BudgetWorkflow.FinalizeOnce(taxedItem, gateway);
            var second = BudgetWorkflow.FinalizeOnce(taxedItem, gateway);
            Console.WriteLine($"First: {(first.IsSuccess ? "OK" : "FAIL")}");
            second.Handle(_ => { },
                e => Console.WriteLine($"Second: [{e.Code}] {e.Message}\n"));

            Console.WriteLine("=== 3. Domain failure (negative) ===");
            BudgetWorkflow.Validate(BudgetWorkflow.Start(-200m)).Handle(
                _ => { },
                e => Console.WriteLine($"[Expected] {e.Code}: {e.Message}\n"));

            Console.WriteLine("=== 4. Boundary failure ===");
            ExpenseDataRepository.LoadFromJson("missing.json").Handle(
                _ => { },
                e => Console.WriteLine($"[Expected] {e.Code}: {e.Message}\n"));

            Console.WriteLine("=== 5. Maybe lookup ===");
            var standardLookup = TaxPolicyLookup.Find("Standard");
            if (standardLookup is Maybe<(decimal Rate, decimal Multiplier)>.Some some)
                Console.WriteLine(
                    $"[Found] Standard rate={some.Value.Rate}, multiplier={some.Value.Multiplier}");

            var missingLookup = TaxPolicyLookup.Find("Nonexistent");
            if (missingLookup is Maybe<(decimal Rate, decimal Multiplier)>.None)
                Console.WriteLine("[Not found] 'Nonexistent'\n");
        }

        private static void RunIntegratedReportsAndNative()
        {
            Console.WriteLine("=== Integrated reports + native C call ===");
            var path = Path.Combine(AppContext.BaseDirectory, "data", "expenses.json");
            ExpenseDataRepository.LoadFromJson(path).Handle(RunReports,
                e => Console.WriteLine($"Load failed: [{e.Code}] {e.Message}"));
        }

        private static void RunReports(System.Collections.Generic.List<ExpenseRecord> expenses)
        {
            var conservative = Scoring.CreateScore()(new ScoreConfig(1, 10, 0));
            var aggressive   = Scoring.CreateScore()(new ScoreConfig(2, 10, 0));

            var adapter = new NativeTaxFeeAdapter();
            BudgetWorkflowRunner.EnrichExpenses(expenses, adapter, 250, conservative).Handle(
                enriched =>
                {
                    var snapshot = BudgetReports.EnumeratePriorityRows(enriched, 0).ToArray();
                    Console.WriteLine($"Materialized priority rows: {snapshot.Length}");

                    BudgetReports.BuildCategorySummary(enriched).Handle(
                        report =>
                        {
                            Console.WriteLine("Category summary:");
                            foreach (var row in report.Rows)
                                Console.WriteLine(
                                    $"  {row.Category}: n={row.ExpenseCount}, " +
                                    $"total=${row.TotalAmount:F2}, tax={row.TotalTaxCents}¢");

                            string outPath = Path.Combine(AppContext.BaseDirectory, "category-report.csv");
                            ReportExporter.ExportCategorySummaryCsv(outPath, report).Handle(
                                p => Console.WriteLine($"Exported: {p}"),
                                e => Console.WriteLine($"Export failed: [{e.Code}]"));
                        },
                        e => Console.WriteLine($"Summary failed: [{e.Code}]"));

                    Console.WriteLine("Same input, two score configs:");
                    foreach (var e in enriched)
                        Console.WriteLine(
                            $"  {e.Id}: conservative={e.Score}, aggressive={aggressive(e)}");
                },
                e => Console.WriteLine($"Enrichment failed: [{e.Code}] {e.Message}"));
        }
    }
}