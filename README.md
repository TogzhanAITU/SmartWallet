# Budget Summary

An expense-processing pipeline written in C# in a functional style, with two front ends: a console demo (plus an xUnit suite) and a Blazor WebAssembly app.

Each expense moves through a small state machine (`Draft → Validated → Taxed → Receipt`). The fee is calculated by a native C function, and the enriched rows feed two reports: a category summary and a priority list. Business errors are returned as values (`Result<T, E>`), optional lookups use `Maybe<T>`, the workflow is pure, and anything with side effects (file loading, P/Invoke, CSV export) sits at the edge.

The doc is split by audience:

- **Just want to run it?** [Requirements](#requirements) and [Running the project](#running-the-project)
- **Using the web app?** [User guide](#user-guide)
- **Changing the code?** [Developer notes](#developer-notes), [API reference](#api-reference), [Testing](#testing)
- **Something broke?** [Troubleshooting](#troubleshooting)

---

## Requirements

| Tool | Version | Needed for |
|---|---|---|
| .NET SDK | 8.0 | everything |
| gcc | any recent | building the native library on Linux/macOS (the console demo and the tests call it) |
| A modern browser | - | the Blazor app |

The Blazor app does not need gcc. Browsers can't load `.so`/`.dll` files, so it uses a C# port of the same fee formula (see [Developer notes](#developer-notes)).

On Fedora/Nobara: `sudo dnf install -y gcc`. On Ubuntu/Debian: `sudo apt install gcc`.

The first build restores NuGet packages, so you need internet access once.

---

## Running the project

All commands are run from the repository root (the folder that contains `BudgetSummary.sln`).

### Linux

```bash
cd Budget_Summary/native
chmod +x build.sh
./build.sh                 # produces libbudget_native.so
cd ../..

dotnet test Budget_Summary/BudgetWorkflow.csproj
dotnet run  --project Budget_Summary
dotnet run  --project BudgetSummary.Blazor
```

The csproj copies `libbudget_native.so` into the output folder if it exists, so build the library *before* `dotnet build`/`dotnet test`.

### Windows

A pre-compiled `Budget_Summary/native/budget_native.dll` is committed, so no C toolchain is needed. The csproj currently only copies the Linux `.so`, so after the first build copy the DLL next to the executable:

```powershell
dotnet build Budget_Summary\BudgetWorkflow.csproj
copy Budget_Summary\native\budget_native.dll Budget_Summary\bin\Debug\net8.0\

dotnet test Budget_Summary\BudgetWorkflow.csproj
dotnet run  --project Budget_Summary
dotnet run  --project BudgetSummary.Blazor
```

To rebuild the DLL from source with MSVC (Developer Command Prompt):

```powershell
cl /LD /O2 /Fe:budget_native.dll Budget_Summary\native\budget_native.c
```

### macOS (untested)

```bash
cd Budget_Summary/native
gcc -O2 -fPIC -shared -Wall -Wextra -o libbudget_native.dylib budget_native.c
```

.NET resolves `DllImport("budget_native")` to `libbudget_native.dylib` on macOS. The csproj has no copy rule for the `.dylib`, so place it in the output folder by hand, same as the Windows DLL. Linux is the main dev environment and nothing here has been run on a Mac.

### What each command does

| Command | Result |
|---|---|
| `dotnet run --project Budget_Summary` | Console demo, prints six sections (see below) and writes `category-report.csv` next to the executable |
| `dotnet test Budget_Summary/BudgetWorkflow.csproj` | Runs the 24 xUnit tests |
| `dotnet run --project BudgetSummary.Blazor` | Starts the web app on `http://localhost:5000/` and opens the browser |

Console demo output, in order: normal workflow, double-finalize rejected, domain failure (negative amount), boundary failure (missing file), `Maybe` lookup, and finally the integrated run that loads `data/expenses.json`, calls the native function, prints the category summary and exports the CSV.

---

## User guide

This section covers the Blazor app. Data lives in memory only, so reloading the page clears everything.

### Navigation

The sidebar has four pages: Dashboard, Expenses, Reports, About. On screens narrower than 768 px it turns into a bar at the bottom.

### Adding expenses (Expenses page)

1. Type a **Category** (required, free text, e.g. `Groceries`).
2. Enter an **Amount** in dollars. It must be greater than zero.
3. Tick **Mandatory** if it's a fixed cost like rent or utilities. This affects both the tax policy totals and the score.
4. Press **Add Expense**.

The app generates the ID itself (`EXP-` followed by six characters). A green banner confirms the add. A red banner shows the error code and message if something was rejected, for example `[INVALID_AMOUNT]`.

Other buttons on the page:

- **Delete** on a row removes that expense.
- **Clear All** empties the list.
- **Load Sample** replaces the whole list with six example expenses. Use it if you just want to see what the reports look like.

### Dashboard

Shows four totals (total spent, number of transactions, number of categories, total of mandatory expenses) and the category summary: one bar per category, sized by its share of the grand total, with the expense count and the tax in cents. With no data it shows an empty state with shortcuts to add an expense or load the sample.

### Reports

**Configuration**

| Control | Values | Effect |
|---|---|---|
| Native Tax Rate (BPS) | 0, 100, 250, 500, 1000 | Rate used for the per-expense fee in the category summary. 100 bps = 1%. |
| Score Preset | Conservative / Aggressive | Conservative scores `amount × 1 + 10 if mandatory`. Aggressive scores `amount × 2 + 10 if mandatory`. |
| Min Priority Score | 0 to 500, step 10 | Rows scoring below this are hidden from the priority table. |

Everything recalculates when a control changes.

**Totals under each tax policy.** Three fixed policies, each applied to every expense. Mandatory expenses pay the base rate, non-mandatory ones pay `rate × multiplier`:

| Policy | Base rate | Multiplier for non-mandatory |
|---|---|---|
| Standard | 10% | 1.5 |
| Discount | 5% | 0.5 |
| Increase | 15% | 2.0 |

The figure shown is amount plus tax, summed over all expenses. This is a different calculation from the tax-in-cents column of the category summary, which uses the basis points from the dropdown, so the two numbers are not expected to match.

**Category summary.** Per category: expense count, total amount, total fee in cents. Sorted by total amount (largest first), ties broken by category name.

**Priority orders.** Expenses with score ≥ the minimum, sorted by score, then amount (both descending), then ID.

### Error messages

Red banners always show `[CODE] message`. The codes are listed in the [error code table](#error-codes).

---

## Developer notes

### Repository layout

```
BudgetSummary.sln
Budget_Summary/                  core library + console demo + tests (one project)
  BudgetWorkflow.cs              state records, Result/Maybe, pure workflow, JSON loader
  BudgetReporting.cs             enrichment, scoring, reports, CSV exporter
  NativeTaxAdapter.cs            ITaxFeeAdapter, P/Invoke adapter, failing test adapter
  ManagedTaxFeeAdapter.cs        pure C# version of the fee formula
  Program.cs                     console demo
  BudgetWorkflowTests.cs         24 xUnit tests
  data/expenses.json             sample data for the console demo
  native/                        budget_native.c/.h, build.sh, prebuilt Windows DLL
BudgetSummary.Blazor/            WebAssembly UI
  Program.cs                     DI setup (adapter + state container)
  Services/BudgetState.cs        state container
  Pages/                         Dashboard, Expenses, Reports, About
  Components/                    ExpenseForm, ExpenseTable, CategorySummary, PriorityTable
  Components/Layout/             MainLayout, NavMenu, StatCard, ResultBanner
  wwwroot/                       index.html, css/app.css
```

The Blazor project references `Budget_Summary/BudgetWorkflow.csproj` directly. Note that `BudgetSummary.sln` only lists the core project, so open or build the Blazor project by path.

### How the pieces fit

```
expenses.json / UI form
        │
        ▼
ExpenseRecord ──ValidateForReporting──▶ EnrichWithTax(ITaxFeeAdapter) ──▶ score func ──▶ EnrichedExpense
                                                                                            │
                                                         BuildCategorySummary ◀─────────────┤
                                                         BuildPriorityReport  ◀─────────────┘
```

Three boundaries keep the core testable:

- **Errors.** Domain failures are `Result<T, DomainError>` values. Nothing in the pipeline throws for bad input. Reading `.Value` on a failed result does throw `InvalidOperationException`, but that means the caller forgot to check `IsSuccess`.
- **Tax fee.** Everything goes through `ITaxFeeAdapter`. The console demo and tests use `NativeTaxFeeAdapter` (P/Invoke into C), the browser uses `ManagedTaxFeeAdapter`, and one test uses `FailingTaxFeeAdapter` to simulate a broken native library.
- **Scoring.** `Scoring.CreateScore()` is curried: give it a `ScoreConfig`, get back a plain `Func<EnrichedExpense, int>`. The runner receives that function, so the scoring rule can change without touching it.

### State management (Blazor)

`BudgetState` is registered as a singleton in `Program.cs` and is the only thing the UI talks to. It holds the expense list, tax rate, score preset and minimum score, and exposes `Enriched()`, `CategoryReport()` and `PriorityReport()` which call the pure core. It raises `OnChange` after every mutation. Pages subscribe in `OnInitialized`, call `InvokeAsync(StateHasChanged)` in the handler, and unsubscribe in `Dispose`. Forgetting the unsubscribe leaks the page, so copy the pattern from an existing page when adding one.

The UI never calls `BudgetWorkflow` or `BudgetReports` directly.

### Components

| Component | Parameters | Notes |
|---|---|---|
| `CategorySummary` | `Result` (required), `TaxBps` | Takes the report as a `Result`, shows an error banner on failure |
| `PriorityTable` | `Result` (required), `MinScore` | Same pattern |
| `StatCard` | `Label`, `Value` (both required), `Icon` | Purely presentational |
| `ResultBanner` | `Message`, `Kind` (`info`/`success`/`error`) | Renders nothing when `Message` is empty; has `role="alert"` |
| `ExpenseForm` | none | Injects `BudgetState`, validates with `DataAnnotations`, then again through `AddExpense` |
| `ExpenseTable` | none | Injects `BudgetState` |

### Extending it

- **New tax adapter:** implement `ITaxFeeAdapter`, then change the `AddSingleton<ITaxFeeAdapter, ...>` line in `BudgetSummary.Blazor/Program.cs`.
- **New score preset:** add a case to `BudgetState.ScoreConfig` and an `<option>` in `Reports.razor`. The preset names are plain strings in both places.
- **New page:** create `Pages/Foo.razor` with an `@page` route, follow the subscribe/dispose pattern above, and add a `NavLink` to `NavMenu.razor`.
- **New error code:** return a `DomainError("YOUR_CODE", "message")` and add it to the table below.

### Decisions that might look odd

- **File handles are released on every path** in `ExpenseDataRepository.LoadFromJson`. A test reopens the file with `FileShare.None` after both a good and a malformed load, which would fail if a reader leaked.
- **Two reports, two sort orders.** Category summary: total amount, then name. Priority: score, then amount, then ID. Both are pinned by tests so a refactor can't change them quietly. ID is only the last tie-break, which keeps output identical between runs.
- **`ExpenseRecord.Quantity`** is loaded from JSON but not used by any report or by the UI. It's there for future use.
- **The native C code and the managed port must agree.** Both compute `(amountCents × bps + 5000) / 10000`, i.e. round half-up. If you change one, change the other.

---

## API reference

Everything below is in the `Budget_Summary` namespace.

### Core types

| Type | Purpose |
|---|---|
| `Result<T, E>` | `IsSuccess`, `Value`, `Error`, `Match(onSuccess, onFailure)`, `Handle(onSuccess, onFailure)`. Create with `Result<T,E>.Success(v)` / `.Failure(e)`. |
| `Maybe<T>` | `Maybe<T>.Some` (has `Value`) or `Maybe<T>.None`. |
| `DomainError(Code, Message)` | The error type used everywhere. |
| `ExpenseDraft` → `ValidatedExpense` → `TaxedExpense` → `ExpenseReceipt` | The workflow states. |
| `ExpenseRecord(Id, Amount)` | Input row. Optional: `Category` (default `"Uncategorized"`), `IsMandatory` (false), `Quantity` (1), `CreatedAt`. |
| `EnrichedExpense` | An `ExpenseRecord` plus `TaxCents` and `Score`. |
| `ScoreConfig(AmountWeight, MandatoryBonus, TaxPenaltyWeight)` | Input to `Scoring.CreateScore()`. Score = `(int)(amount × AmountWeight) + (mandatory ? MandatoryBonus : 0) − (TaxCents / 100) × TaxPenaltyWeight`. Both built-in presets use a tax penalty of 0, so tax doesn't change scores today. |

### Functions

| Function | Signature (short) | What it does |
|---|---|---|
| `BudgetWorkflow.Start` | `(decimal, bool) → ExpenseDraft` | Creates a draft |
| `BudgetWorkflow.Validate` | `ExpenseDraft → Result<ValidatedExpense>` | Rejects amounts ≤ 0 |
| `BudgetWorkflow.ApplyTaxPolicy` | `(ValidatedExpense, rate, multiplier) → Result<TaxedExpense>` | Rate 0 to 1. Mandatory expenses pay `rate`, others pay `rate × multiplier`. Rounds to 2 decimals. |
| `BudgetWorkflow.Map` / `Bind` | `Result<T,E>` + function | Chain steps; failures skip the function |
| `BudgetWorkflow.Finalize` | `(TaxedExpense, Func<decimal,string>) → Result<ExpenseReceipt>` | Calls the gateway, turns exceptions into `GATEWAY_ERROR` |
| `BudgetWorkflow.FinalizeOnce` | same | Like `Finalize`, but a second call for the same `TaxedExpense` fails with `ALREADY_FINALIZED` |
| `ExpenseDataRepository.LoadFromJson` | `string path → Result<List<ExpenseRecord>>` | Reads a JSON array of expenses |
| `TaxPolicyLookup.Find` | `string → Maybe<(Rate, Multiplier)>` | `"Standard"`, `"Discount"`, `"Increase"` |
| `BudgetWorkflowRunner.EnrichExpenses` | `(expenses, adapter, taxBps, scoreFn) → Result<IReadOnlyList<EnrichedExpense>>` | Validate, compute fee, score. Stops at the first failure. |
| `BudgetReports.BuildCategorySummary` | `IEnumerable<EnrichedExpense> → Result<CategorySummaryReport>` | Rows plus `GrandTotal` |
| `BudgetReports.BuildPriorityReport` | `(expenses, minScore) → Result<PriorityReport>` | Filtered, sorted rows |
| `BudgetReports.EnumeratePriorityRows` | `(expenses, minScore) → IEnumerable<PriorityRow>` | Lazy version (uses `yield return`) |
| `ReportExporter.ExportCategorySummaryCsv` | `(path, report) → Result<string>` | Writes `Category,ExpenseCount,TotalAmount,TotalTaxCents`, returns the path |

### Data file format

`data/expenses.json` is an array. Use the same property names as the sample file:

```json
[
  { "Id": "EXP-001", "Amount": 150.50, "Category": "Groceries",
    "IsMandatory": true, "Quantity": 1, "CreatedAt": "2025-01-01T00:00:00Z" }
]
```

`Id` and `Amount` are the constructor arguments of `ExpenseRecord`, so always provide them. The rest have defaults (see `ExpenseRecord` above). Reporting rejects empty IDs, empty categories and amounts ≤ 0.

### Native function

```c
int32_t expense_tax_cents(int32_t amount_cents, int32_t tax_bps);
```

Returns the fee in cents, rounded half-up, or `-1` for negative amounts or `tax_bps` outside 0 to 5000 (and on overflow). Example: `expense_tax_cents(10000, 250) == 250`, i.e. $100.00 at 2.5% is $2.50.

`NativeTaxFeeAdapter` also checks the amount first: it must be a whole number of cents and fit in an `int`. Library problems are reported as distinct error codes instead of exceptions (see below).

### Error codes

| Where | Codes |
|---|---|
| Workflow | `INVALID_AMOUNT`, `INVALID_TAX_RATE`, `INVALID_MULTIPLIER`, `GATEWAY_ERROR`, `ALREADY_FINALIZED` |
| File loading | `FILE_NOT_FOUND`, `MALFORMED_FILE`, `MALFORMED_JSON`, `BOUNDARY_EXCEPTION` |
| Validation / reports | `NULL_EXPENSES`, `NULL_EXPENSE`, `INVALID_ID`, `INVALID_CATEGORY`, `INVALID_AMOUNT`, `INVALID_STRING`, `INVALID_TAX`, `NULL_ADAPTER` |
| Tax adapters | `INVALID_AMOUNT`, `INVALID_TAX_BPS`, `INVALID_AMOUNT_PRECISION`, `AMOUNT_TOO_LARGE`, `TAX_TOO_LARGE` (managed only) |
| Native only | `NATIVE_INVALID_INPUT`, `NATIVE_LIBRARY_NOT_FOUND`, `NATIVE_ENTRYPOINT_NOT_FOUND`, `NATIVE_BAD_IMAGE`, `NATIVE_CALL_FAILED` |
| CSV export | `INVALID_PATH`, `NULL_REPORT`, `EXPORT_FAILED` |
| Blazor state | `DUPLICATE_ID`, `POLICY_NOT_FOUND` (plus `INVALID_ID` / `INVALID_CATEGORY` / `INVALID_AMOUNT` from the form) |
| Tests only | `NATIVE_UNAVAILABLE` (default code of `FailingTaxFeeAdapter`) |

---

## Testing

```bash
dotnet test Budget_Summary/BudgetWorkflow.csproj
```

24 xUnit tests in `BudgetWorkflowTests.cs`. The native library has to be present (see [Running the project](#running-the-project)), otherwise the native-call tests fail with `NATIVE_LIBRARY_NOT_FOUND`.

What they cover:

- **Workflow and `Result`:** happy path, invalid amount/rate, `Map`/`Bind` skipping their callbacks after a failure (two counters must stay at 0), `FinalizeOnce` rejecting a repeat.
- **Loading:** missing file, malformed JSON, file handle released after success and failure.
- **Reports:** group totals, empty input, tied values giving a stable order, CSV export success and failure.
- **Laziness:** `yield return` doesn't run before enumeration; `ToArray()` snapshots aren't re-enumerated.
- **Native boundary:** a real call returns 250 for `(10000, 250)`; a failing adapter surfaces its error and leaves the input array untouched.

What isn't covered, so you know where to look first if something breaks:

- `ManagedTaxFeeAdapter` has no tests of its own. Its output is only checked indirectly, by hand, in the UI.
- `BudgetState` and the Razor components have no tests. There are no integration or browser tests and no coverage measurement.

Manual check after UI changes: load the sample data, add an expense, try amount `0` and an empty category (both should be rejected), delete one row, change each Reports control and confirm both tables update, then shrink the window below 768 px and check the bottom navigation.

---

## Troubleshooting

| Symptom | Likely cause | Fix |
|---|---|---|
| `NATIVE_LIBRARY_NOT_FOUND` in the console or tests | `libbudget_native.so` (or the DLL) isn't in the output folder | Linux: run `native/build.sh`, then rebuild. Windows: copy the DLL as described above. |
| `NATIVE_BAD_IMAGE` | Library was built for a different architecture (e.g. 32-bit DLL on a 64-bit runtime) | Rebuild it for the same architecture as your .NET runtime |
| `FILE_NOT_FOUND` for `expenses.json` in the console demo | The data file wasn't copied to the output folder | Rebuild; the csproj copies `data/expenses.json` on build |
| `gcc: command not found` | No compiler installed | Install gcc (see Requirements) |
| Blazor app won't start, port in use | Something else is on port 5000 | Stop it, or change `applicationUrl` in `BudgetSummary.Blazor/Properties/launchSettings.json` |
| Page loads but stays on the "Loading" splash | The WebAssembly build failed or the browser cached an old one | Check the terminal for build errors, then hard-reload |

---

## Known limitations

- `BudgetWorkflow._finalized` is a static `HashSet` used for the idempotency check. Fine for a single-process demo, but it would have to be scoped or persisted in a real service.
- `ReportExporter` writes CSV with no locking, so concurrent writes to one path aren't handled.
- The Web UI keeps everything in memory. There's no persistence and no way to import or export from the browser.
- The Windows DLL is committed as a binary because the MSVC toolchain version matters. The `.c` and `.h` are the source of truth, the DLL is a build artifact. The csproj doesn't copy it automatically yet.
- macOS is untested.
- `BudgetSummary.sln` doesn't include the Blazor project.