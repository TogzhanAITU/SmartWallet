using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;
using Budget_Summary;

namespace Budget_Summary.Tests
{
    public class BudgetWorkflowTests
    {
        [Fact]
        public void CompleteSuccess_Workflow_ProducesCorrectTaxedAmount()
        {
            var validated = BudgetWorkflow.Validate(BudgetWorkflow.Start(1000m, true));
            var taxed = BudgetWorkflow.Bind(validated, v => BudgetWorkflow.ApplyTaxPolicy(v, 0.10m, 1.5m));
            var receipt = BudgetWorkflow.Bind(taxed, t => BudgetWorkflow.FinalizeOnce(t, _ => "TXN-1"));

            Assert.True(receipt.IsSuccess);
            Assert.Equal(1100m, receipt.Value.Amount);
            Assert.Equal(100m, receipt.Value.TaxAmount);
        }

        [Fact]
        public void Validate_NegativeAmount_TriggersDomainValidationFailure()
        {
            var r = BudgetWorkflow.Validate(BudgetWorkflow.Start(-50m));
            Assert.False(r.IsSuccess);
            Assert.Equal("INVALID_AMOUNT", r.Error.Code);
        }

        [Fact]
        public void ApplyTaxPolicy_InvalidRate_TriggersDomainFailure()
        {
            var r = BudgetWorkflow.ApplyTaxPolicy(new ValidatedExpense(100m, false), 1.5m, 1m);
            Assert.False(r.IsSuccess);
            Assert.Equal("INVALID_TAX_RATE", r.Error.Code);
        }

        [Fact]
        public void Lookup_PresentAndAbsentValues_HandledCorrectly()
        {
            var present = TaxPolicyLookup.Find("Standard");
            var absent  = TaxPolicyLookup.Find("Nope");
            Assert.IsType<Maybe<(decimal, decimal)>.Some>(present);
            Assert.IsType<Maybe<(decimal, decimal)>.None>(absent);
        }

        [Fact]
        public void Map_TransformsSuccessfulValueCorrectly()
        {
            var r = Result<decimal, DomainError>.Success(100m);
            var m = BudgetWorkflow.Map(r, x => $"Total: ${x}");
            Assert.True(m.IsSuccess);
            Assert.Equal("Total: $100", m.Value);
        }

        [Fact]
        public void Bind_ChainsAndPreservesSuccessfulResult()
        {
            var v = BudgetWorkflow.Validate(BudgetWorkflow.Start(200m));
            var r = BudgetWorkflow.Bind(v, x => BudgetWorkflow.ApplyTaxPolicy(x, 0.5m, 1m));
            Assert.True(r.IsSuccess);
            Assert.Equal(300m, r.Value.Amount);
        }

        [Fact]
        public void FailurePropagation_MapAndBindSkipExecution()
        {
            int m = 0, b = 0;
            var failed = Result<ValidatedExpense, DomainError>.Failure(
                new DomainError("ERR", "Failed"));

            var mapped = BudgetWorkflow.Map(failed, v => { m++; return v.Amount; });
            var bound  = BudgetWorkflow.Bind(failed, v => { b++; return BudgetWorkflow.ApplyTaxPolicy(v, 0.1m, 1m); });

            Assert.False(mapped.IsSuccess);
            Assert.False(bound.IsSuccess);
            Assert.Equal(0, m);
            Assert.Equal(0, b);
        }

        [Fact]
        public void FinalizeOnce_PreventsDoubleFinalizing()
        {
            var taxed = new TaxedExpense(500m, 50m);
            var first  = BudgetWorkflow.FinalizeOnce(taxed, _ => "TXN-A");
            var second = BudgetWorkflow.FinalizeOnce(taxed, _ => "TXN-B");
            Assert.True(first.IsSuccess);
            Assert.False(second.IsSuccess);
            Assert.Equal("ALREADY_FINALIZED", second.Error.Code);
        }

        [Fact]
        public void LoadFromJson_MissingFile_ReturnsFileNotFoundFailure()
        {
            var r = ExpenseDataRepository.LoadFromJson("nope.json");
            Assert.False(r.IsSuccess);
            Assert.Equal("FILE_NOT_FOUND", r.Error.Code);
        }

        [Fact]
        public void LoadFromJson_MalformedFile_ReturnsMalformedJsonFailure()
        {
            string tmp = Path.GetTempFileName();
            File.WriteAllText(tmp, "{ not valid ...");
            var r = ExpenseDataRepository.LoadFromJson(tmp);
            File.Delete(tmp);
            Assert.False(r.IsSuccess);
            Assert.Equal("MALFORMED_JSON", r.Error.Code);
        }

        [Fact]
        public void ValidateForReporting_Extension_ValidatesAndMaterializes()
        {
            var valid = new[] { new ExpenseRecord("A", 10m) { Category = "X" } };
            Assert.True(valid.ValidateForReporting().IsSuccess);

            var invalid = new[] { new ExpenseRecord("A", -1m) { Category = "X" } };
            var fail = invalid.ValidateForReporting();
            Assert.False(fail.IsSuccess);
            Assert.Equal("INVALID_AMOUNT", fail.Error.Code);
        }

        [Fact]
        public void EnrichWithTax_Extension_UsesAdapterAndReturnsTax()
        {
            var e = new ExpenseRecord("A", 100m) { Category = "X" };
            var r = e.EnrichWithTax(new NativeTaxFeeAdapter(), 250);
            Assert.True(r.IsSuccess);
            Assert.Equal(250, r.Value.TaxCents);
        }

        [Fact]
        public void ConfiguredScores_SameInput_DifferentResults()
        {
            var e = new EnrichedExpense("A", 100m, "Books", true, 250, 0);
            var conservative = Scoring.CreateScore()(new ScoreConfig(1, 10, 0));
            var aggressive   = Scoring.CreateScore()(new ScoreConfig(2, 10, 0));

            Assert.NotEqual(conservative(e), aggressive(e));
            Assert.Equal(110, conservative(e));
            Assert.Equal(210, aggressive(e));
        }

        [Fact]
        public void DeferredExecution_DoesNotProcessBeforeEnumeration()
        {
            var order = new EnrichedExpense("A", 100m, "Books", true, 250, 0);
            var source = new CountingEnumerable<EnrichedExpense>(new[] { order });

            var q = BudgetReports.EnumeratePriorityRows(source, 0);
            Assert.Equal(0, source.MoveNextCount);
            Assert.Equal(0, source.GetEnumeratorCount);

            var first = q.First();
            Assert.Equal("A", first.Id);
            Assert.True(source.MoveNextCount > 0);
        }

        [Fact]
        public void MaterializedSnapshot_ReuseDoesNotReenumerateSource()
        {
            var order = new EnrichedExpense("A", 100m, "Books", true, 250, 0);
            var source = new CountingEnumerable<EnrichedExpense>(new[] { order });

            var snap = BudgetReports.EnumeratePriorityRows(source, 0).ToArray();
            int after = source.MoveNextCount;
            _ = snap.Length;
            _ = snap.First();
            Assert.Equal(after, source.MoveNextCount);
        }

        [Fact]
        public void NativeAdapter_RealCall_ReturnsExpectedCents()
        {
            var r = new NativeTaxFeeAdapter().ComputeTaxCents(100m, 250);
            Assert.True(r.IsSuccess);
            Assert.Equal(250, r.Value);
        }

        [Fact]
        public void FailingAdapter_ReturnsClearFailure()
        {
            var r = new FailingTaxFeeAdapter("NATIVE_UNAVAILABLE", "simulated").ComputeTaxCents(100m, 250);
            Assert.False(r.IsSuccess);
            Assert.Equal("NATIVE_UNAVAILABLE", r.Error.Code);
        }

        [Fact]
        public void CompleteWorkflow_ReportsAndNative_NormalPath()
        {
            var expenses = new[]
            {
                new ExpenseRecord("B", 50m)  { Category = "Books", IsMandatory = true  },
                new ExpenseRecord("A", 100m) { Category = "Books", IsMandatory = true  },
                new ExpenseRecord("C", 75m)  { Category = "Games", IsMandatory = false }
            };
            var score = Scoring.CreateScore()(new ScoreConfig(1, 10, 0));
            var enriched = BudgetWorkflowRunner.EnrichExpenses(expenses, new NativeTaxFeeAdapter(), 250, score);
            Assert.True(enriched.IsSuccess);

            var summary = BudgetReports.BuildCategorySummary(enriched.Value);
            Assert.True(summary.IsSuccess);
            Assert.Equal(2, summary.Value.Rows.Count);

            var books = summary.Value.Rows.Single(r => r.Category == "Books");
            Assert.Equal(2, books.ExpenseCount);
            Assert.Equal(150m, books.TotalAmount);
            Assert.Equal(375, books.TotalTaxCents);

            var priority = BudgetReports.BuildPriorityReport(enriched.Value, 0);
            Assert.True(priority.IsSuccess);
            Assert.Equal(3, priority.Value.Rows.Count);
        }

        [Fact]
        public void CompleteWorkflow_NativeFailure_PreservesState()
        {
            var expenses = new[] { new ExpenseRecord("A", 100m) { Category = "X" } };
            var original = expenses.ToArray();
            var score = Scoring.CreateScore()(new ScoreConfig(1, 10, 0));

            var r = BudgetWorkflowRunner.EnrichExpenses(
                expenses, new FailingTaxFeeAdapter("NATIVE_UNAVAILABLE", "sim"), 250, score);

            Assert.False(r.IsSuccess);
            Assert.Equal("NATIVE_UNAVAILABLE", r.Error.Code);
            Assert.Equal(original, expenses);
        }

        [Fact]
        public void Report_EmptyAndNoMatches_AreSafe()
        {
            var empty = BudgetReports.BuildCategorySummary(Array.Empty<EnrichedExpense>());
            Assert.True(empty.IsSuccess);
            Assert.Empty(empty.Value.Rows);

            var noMatch = BudgetReports.BuildPriorityReport(
                new[] { new EnrichedExpense("A", 100m, "X", true, 250, 5) }, 9999);
            Assert.True(noMatch.IsSuccess);
            Assert.Empty(noMatch.Value.Rows);
        }

        [Fact]
        public void Report_TiedPrimaryValues_DeterministicOrder()
        {
            var tied = new[]
            {
                new EnrichedExpense("B", 10m, "Z", true, 0, 5),
                new EnrichedExpense("A", 10m, "A", true, 0, 5)
            };
            var pr = BudgetReports.BuildPriorityReport(tied, 0);
            Assert.Equal(new[] { "A", "B" }, pr.Value.Rows.Select(r => r.Id)); // Score DESC → Amount DESC → Id ASC
            var catTied = new[]
            {
                new EnrichedExpense("1", 10m, "Z", true, 0, 0),
                new EnrichedExpense("2", 10m, "A", true, 0, 0)
            };
            var cr = BudgetReports.BuildCategorySummary(catTied);
            Assert.Equal(new[] { "A", "Z" }, cr.Value.Rows.Select(r => r.Category));
        }

        [Fact]
        public void Report_GroupTotalsCorrect()
        {
            var rows = new[]
            {
                new EnrichedExpense("A", 10m, "Books", true, 25, 1),
                new EnrichedExpense("B", 20m, "Books", true, 50, 2),
                new EnrichedExpense("C", 5m,  "Games", false, 13, 3)
            };
            var r = BudgetReports.BuildCategorySummary(rows);
            Assert.True(r.IsSuccess);
            var books = r.Value.Rows.Single(x => x.Category == "Books");
            Assert.Equal(2, books.ExpenseCount);
            Assert.Equal(30m, books.TotalAmount);
            Assert.Equal(75, books.TotalTaxCents);
            Assert.Equal(35m, r.Value.GrandTotal);
        }

        [Fact]
        public void LoadFromJson_SuccessAndMalformed_ReleaseObservableFileHandle()
        {
            string good = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
            File.WriteAllText(good, "[{\"Id\":\"A\",\"Amount\":10,\"Category\":\"X\"}]");
            Assert.True(ExpenseDataRepository.LoadFromJson(good).IsSuccess);
            using (var fs = new FileStream(good, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                Assert.True(fs.CanRead);
            File.Delete(good);

            string bad = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
            File.WriteAllText(bad, "{ bad");
            Assert.False(ExpenseDataRepository.LoadFromJson(bad).IsSuccess);
            using (var fs = new FileStream(bad, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                Assert.True(fs.CanRead);
            File.Delete(bad);
        }

        [Fact]
        public void ExportCategorySummaryCsv_SuccessAndFailure()
        {
            var report = new CategorySummaryReport(
                new[] { new CategorySummaryRow("Books", 2, 30m, 75) }, 30m);

            string path = Path.GetTempFileName();
            Assert.True(ReportExporter.ExportCategorySummaryCsv(path, report).IsSuccess);
            Assert.Contains("Books", File.ReadAllText(path));
            File.Delete(path);

            string badPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString(), "r.csv");
            var bad = ReportExporter.ExportCategorySummaryCsv(badPath, report);
            Assert.False(bad.IsSuccess);
            Assert.Equal("EXPORT_FAILED", bad.Error.Code);
        }

        private sealed class CountingEnumerable<T> : IEnumerable<T>
        {
            private readonly IEnumerable<T> _source;
            public int MoveNextCount;
            public int GetEnumeratorCount;
            public CountingEnumerable(IEnumerable<T> source) => _source = source;

            public IEnumerator<T> GetEnumerator()
            {
                GetEnumeratorCount++;
                foreach (var item in _source) { MoveNextCount++; yield return item; }
            }
            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        }
    }
}