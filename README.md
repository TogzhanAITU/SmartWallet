```markdown
# Budget Summary

Console app plus an xUnit test suite for an expense-processing pipeline.

Each expense goes through a small state machine (`Draft → Validated → Taxed → Receipt`),
the taxed amount is sent through a native C function for the fee calculation, and
the enriched rows feed two reports: a category summary and a priority list.

The project is written in functional style. Business errors are return values
(`Result<T, E>`), optional lookups are `Maybe<T>`, the workflow itself is pure,
and side effects (file loading, P/Invoke, CSV export) sit at the boundary.

There's also a Blazor WebAssembly UI (`BudgetSummary.Blazor/`) that sits on top
of the same core — see the bottom of this file.

---

## Running it

### Linux / macOS

```
cd native
chmod +x build.sh
./build.sh          # builds libbudget_native.so
cd ..
dotnet build
dotnet test
dotnet run
```

Requires `gcc`. On Fedora/Nobara: `sudo dnf install -y gcc`.

### Windows

The pre-compiled Windows binary `native/budget_native.dll` is committed, so no
build step is needed:

```powershell
dotnet build
dotnet test
dotnet run
```

The csproj copies the DLL next to the executable so `DllImport("budget_native")`
resolves it at runtime. To rebuild it from source with MSVC:

```powershell
cl /LD /O2 /Fe:budget_native.dll native\budget_native.c
```

### macOS (untested)

Build a `.dylib`:

```bash
cd native
gcc -O2 -fPIC -shared -Wall -Wextra -o libbudget_native.dylib budget_native.c
```

.NET resolves `DllImport("budget_native")` to `libbudget_native.dylib` on
macOS, so that name works. Linux is the primary dev environment; treat macOS
as untested.

---

## File map

| File | What lives there |
|---|---|
| `BudgetWorkflow.cs` | State records (`ExpenseDraft`, `ValidatedExpense`, `TaxedExpense`, `ExpenseReceipt`), `Result<T, E>`, `Maybe<T>`, `DomainError`, the pure workflow, and `ExpenseDataRepository` (file-loading boundary) |
| `BudgetReporting.cs` | `EnrichedExpense`, both report builders, the curried score function, `ValidateForReporting` / `EnrichWithTax` extensions, the CSV exporter |
| `NativeTaxAdapter.cs` | `ITaxFeeAdapter`, the P/Invoke adapter, and a failing adapter used in tests |
| `ManagedTaxFeeAdapter.cs` | Pure-C# implementation of the same fee formula, used by the browser build |
| `native/` | C source, header, build script, pre-compiled Windows DLL |
| `data/expenses.json` | Sample data the demo run loads |
| `BudgetWorkflowTests.cs` | 24 xUnit tests |

---

## Things that might surprise you

**Domain failures don't throw.** Reading `result.Value` on a failure throws
`InvalidOperationException`, but that's a programmer error — you were supposed
to check `IsSuccess` first. Business failures like `INVALID_AMOUNT` come back
as a normal `Result` value; nothing unwinds the stack.

**The file-loading boundary releases handles on every path.** This is tested
by reopening the same file with `FileShare.None` after a successful load and
after a malformed one. If either reader leaked, the reopen would fail. See
`LoadFromJson_SuccessAndMalformed_ReleaseObservableFileHandle`.

**Two reports sort ties differently.** Category summary is by total amount,
then category name. Priority is by score, then amount, then ID. Both orders
are pinned in tests so a refactor can't silently change them.

**Scoring is curried on purpose.** `Scoring.CreateScore()(new ScoreConfig(1, 10, 0))`
returns a plain `Func<EnrichedExpense, int>`. That's what gets passed into
`BudgetWorkflowRunner.EnrichExpenses`, so the scoring rule can change without
touching the runner. The demo builds a conservative and an aggressive config
against the same data to make the difference visible.

**Tie-break on priority uses ID, not amount.** `Report_TiedPrimaryValues_DeterministicOrder`
covers this — when score and amount are both equal, the smaller ID sorts first.
That keeps `BuildPriorityReport` deterministic across runs.

---

## Native contract

```c
int32_t expense_tax_cents(int32_t amount_cents, int32_t tax_bps);
```

Returns the fee in cents, rounded half-up. Returns `-1` for out-of-range input
(negative amount, tax basis points outside 0–5000). The C# adapter translates
load-time errors into distinct codes — `NATIVE_LIBRARY_NOT_FOUND`,
`NATIVE_ENTRYPOINT_NOT_FOUND`, `NATIVE_BAD_IMAGE`, `NATIVE_CALL_FAILED` — so
the failure mode is visible in the `DomainError`.

Sanity example: `expense_tax_cents(10000, 250) == 250`. That's $100.00 at 2.5%
= $2.50 = 250 cents. `NativeAdapter_RealCall_ReturnsExpectedCents` asserts
that exact value against the compiled library.

---

## Tests worth pointing at

`dotnet test` runs all 24. The ones that prove something beyond "happy path works":

- `FailurePropagation_MapAndBindSkipExecution` — two counters incremented inside
  the `Map`/`Bind` callbacks; both must stay at 0 after the chain.
- `FinalizeOnce_PreventsDoubleFinalizing` — the idempotency set is exercised,
  not just declared.
- `LoadFromJson_SuccessAndMalformed_ReleaseObservableFileHandle` — see the
  note above about handle release.
- `DeferredExecution_DoesNotProcessBeforeEnumeration` — a counting `IEnumerable<T>`
  proves `yield return` doesn't run before you iterate.
- `MaterializedSnapshot_ReuseDoesNotReenumerateSource` — `ToArray()` freezes
  the sequence so reuse reads the array, not the source.
- `NativeAdapter_RealCall_ReturnsExpectedCents` — calls into the compiled
  library, asserts the returned value.
- `CompleteWorkflow_NativeFailure_PreservesState` — when the adapter fails,
  the input array is untouched.

---

## Limitations

- `BudgetWorkflow._finalized` is a static `HashSet` for idempotency. Fine for
  a single-process demo; would need to be scoped or persisted in a real service.
- `ReportExporter` writes CSV without any locking. Concurrent writes to the
  same path aren't handled — the demo doesn't need them.
- The Windows DLL is checked in as a binary. Building it from source with MSVC
  is scriptable but the toolchain version matters, so committing it is the
  pragmatic choice. The `.c` and `.h` are still the source of truth — the DLL
  is the build artifact.
- macOS support is untested. The `.dylib` build command is above if you need it.

---

## Blazor UI

`BudgetSummary.Blazor/` puts a WebAssembly UI on top of the same core. It swaps
`NativeTaxFeeAdapter` for `ManagedTaxFeeAdapter` because browsers can't load
`.so` or `.dll` files — the managed fallback uses the same rounding rules and
is covered by the same native-side test values (`NativeAdapter_RealCall_ReturnsExpectedCents`).

```bash
dotnet run --project BudgetSummary.Blazor
```

Opens on `http://localhost:5000/`. Pages:

- **Dashboard** — totals and the category bar chart, built from `BudgetReports.BuildCategorySummary`
- **Expenses** — add/remove rows, validated through `BudgetState.AddExpense` returning `Result<bool, DomainError>`
- **Reports** — tax rate in basis points, scoring preset (conservative/aggressive), priority score threshold; recomputes on change
- **About** — one-page explanation of the architecture

The state container (`BudgetState`) holds an `IReadOnlyList<ExpenseRecord>` and
raises `OnChange`; pages subscribe in `OnInitialized` and unsubscribe in
`Dispose`. The UI never calls `BudgetWorkflow` or `BudgetReports` directly —
it goes through the state container, which itself only calls the pure core.
