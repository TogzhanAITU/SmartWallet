# Budget Summary — Functional Pipeline, Native Tax & Deferred Reports

This is a C#/.NET console + xUnit project that demonstrates a **functional
pipeline for expense processing** with a real native C call, deferred LINQ,
curried configuration, and railway-oriented error handling.

## Build / Run / Test

chmod +x native/build.sh
./native/build.sh          # builds libbudget_native.so
dotnet build
dotnet test
dotnet run

## File Map

| File | Responsibility |
|---|---|
| `BudgetWorkflow.cs` | States (`ExpenseDraft → ValidatedExpense → TaxedExpense → ExpenseReceipt`), `Result<T,E>`, `Maybe<T>`, `DomainError`, `ExpenseRecord`, `BudgetWorkflow` (pure pipeline), `ExpenseDataRepository` (boundary I/O), `TaxPolicyLookup` |
| `BudgetReporting.cs` | `EnrichedExpense`, two reports (category summary + priority), curried `Scoring.CreateScore()`, extensions `ValidateForReporting` / `EnrichWithTax`, `BudgetWorkflowRunner`, `ReportExporter` (CSV) |
| `NativeTaxAdapter.cs` | `ITaxFeeAdapter`, `NativeTaxFeeAdapter` (P/Invoke), `FailingTaxFeeAdapter` |
| `native/budget_native.c` / `.h` / `build.sh` | C implementation of `expense_tax_cents`, header, gcc build |
| `data/expenses.json` | Sample data for the reporting demo |
| `BudgetWorkflowTests.cs` | 24 xUnit tests covering every workflow, extension, adapter, report, deferral, and resource-release behavior |

## Behavioral Contracts

**Pure workflow**
- `Draft(Amount)` with tax rate in `[0,1]` → `Validated` → `Taxed` → `Receipt`.
- `Draft(amount ≤ 0)` → `INVALID_AMOUNT`.
- Tax rate outside `[0,1]` → `INVALID_TAX_RATE`.
- `FinalizeOnce(Taxed)` twice → second returns `ALREADY_FINALIZED`.

**Boundary**
- Missing JSON → `FILE_NOT_FOUND`.
- Malformed JSON → `MALFORMED_JSON` (no partial state).
- File handle is released on success **and** on the malformed path (proven by
  reopening with `FileShare.None` in `LoadFromJson_SuccessAndMalformed_ReleaseObservableFileHandle`).

**Extensions (custom)**
- `IEnumerable<ExpenseRecord>.ValidateForReporting()` returns
  `Result<IReadOnlyList<ExpenseRecord>, DomainError>` — a full-list validator.
- `ExpenseRecord.EnrichWithTax(ITaxFeeAdapter, int)` returns
  `Result<EnrichedExpense, DomainError>` — per-item adapter composition.
- Preserved `WhereCategory` / `SumMandatory` on `IEnumerable<EnrichedExpense>`.

**Two LINQ reports**

1. **Category summary** — filter positive amounts, `GroupBy(Category)`,
   aggregate `Count/Sum/Sum`, sort `TotalAmount DESC, Category ASC`.
   Deterministic for ties (proved by `Report_TiedPrimaryValues_DeterministicOrder`).
2. **Priority** — filter `Score ≥ minScore`, sort `Score DESC, Amount DESC, Id ASC`.
   Empty input or no matches → empty `Rows`, never an error.

**Curried scoring**
```csharp
var conservative = Scoring.CreateScore()(new ScoreConfig(1, 10, 0));
var aggressive   = Scoring.CreateScore()(new ScoreConfig(2, 10, 0));
// same input → different score. No mutable global.