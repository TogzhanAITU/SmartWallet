using Budget_Summary;

namespace BudgetSummary.Blazor.Services;

public class BudgetState
{
    private readonly List<ExpenseRecord> _expenses = new();
    private readonly ITaxFeeAdapter _adapter;

    private int _taxBps = 250;
    private string _scorePreset = "conservative";
    private int _minPriorityScore = 0;

    public BudgetState(ITaxFeeAdapter adapter) => _adapter = adapter;

    public IReadOnlyList<ExpenseRecord> Expenses => _expenses.AsReadOnly();
    public int TaxBps => _taxBps;
    public string ScorePresetName => _scorePreset;
    public int MinPriorityScore => _minPriorityScore;

    public ScoreConfig ScoreConfig => _scorePreset switch
    {
        "aggressive" => new ScoreConfig(2, 10, 0),
        _            => new ScoreConfig(1, 10, 0)
    };

    public event Action? OnChange;

    public Result<bool, DomainError> AddExpense(ExpenseRecord expense)
    {
        if (string.IsNullOrWhiteSpace(expense.Id))
            return Result<bool, DomainError>.Failure(new DomainError("INVALID_ID", "Id is required."));
        if (string.IsNullOrWhiteSpace(expense.Category))
            return Result<bool, DomainError>.Failure(new DomainError("INVALID_CATEGORY", "Category is required."));
        if (expense.Amount <= 0)
            return Result<bool, DomainError>.Failure(new DomainError("INVALID_AMOUNT", "Amount must be > 0."));
        if (_expenses.Any(e => e.Id == expense.Id))
            return Result<bool, DomainError>.Failure(new DomainError("DUPLICATE_ID", $"Id '{expense.Id}' already exists."));

        _expenses.Add(expense);
        Notify();
        return Result<bool, DomainError>.Success(true);
    }

    public void RemoveAt(int index)
    {
        if (index >= 0 && index < _expenses.Count) { _expenses.RemoveAt(index); Notify(); }
    }

    public void ClearAll() { _expenses.Clear(); Notify(); }

    public void SetTaxBps(int bps)
    {
        if (bps < 0 || bps > 5000) return;
        _taxBps = bps;
        Notify();
    }

    public void SetScorePreset(string preset) { _scorePreset = preset; Notify(); }
    public void SetMinPriorityScore(int min) { _minPriorityScore = min; Notify(); }

    public void LoadSampleData()
    {
        _expenses.Clear();
        _expenses.AddRange(new[]
        {
            new ExpenseRecord("EXP-001", 150.50m) { Category = "Groceries",     IsMandatory = true,  Quantity = 1 },
            new ExpenseRecord("EXP-002",  99.99m) { Category = "Entertainment", IsMandatory = false, Quantity = 1 },
            new ExpenseRecord("EXP-003", 450.00m) { Category = "Groceries",     IsMandatory = true,  Quantity = 2 },
            new ExpenseRecord("EXP-004",  75.25m) { Category = "Transport",     IsMandatory = true,  Quantity = 3 },
            new ExpenseRecord("EXP-005", 220.00m) { Category = "Utilities",     IsMandatory = true,  Quantity = 1 },
            new ExpenseRecord("EXP-999",  60.00m) { Category = "Entertainment", IsMandatory = false, Quantity = 2 }
        });
        Notify();
    }

    public Result<IReadOnlyList<EnrichedExpense>, DomainError> Enriched() =>
        BudgetWorkflowRunner.EnrichExpenses(
            _expenses, _adapter, _taxBps, Scoring.CreateScore()(ScoreConfig));

    public Result<CategorySummaryReport, DomainError> CategoryReport() =>
        Enriched().Match(
            list => BudgetReports.BuildCategorySummary(list),
            err  => Result<CategorySummaryReport, DomainError>.Failure(err));

    public Result<PriorityReport, DomainError> PriorityReport() =>
        Enriched().Match(
            list => BudgetReports.BuildPriorityReport(list, _minPriorityScore),
            err  => Result<PriorityReport, DomainError>.Failure(err));

    public Result<decimal, DomainError> TotalUnderTaxPolicy(string policyName)
    {
        var policy = TaxPolicyLookup.Find(policyName);
        if (policy is not Maybe<(decimal Rate, decimal Multiplier)>.Some some)
            return Result<decimal, DomainError>.Failure(
                new DomainError("POLICY_NOT_FOUND", $"Tax policy '{policyName}' not found."));

        decimal total = 0;
        foreach (var e in _expenses)
        {
            var result = BudgetWorkflow.ApplyTaxPolicy(
                new ValidatedExpense(e.Amount, e.IsMandatory),
                some.Value.Rate,
                some.Value.Multiplier);
            if (!result.IsSuccess) return Result<decimal, DomainError>.Failure(result.Error);
            total += result.Value.Amount;
        }
        return Result<decimal, DomainError>.Success(total);
    }

    private void Notify() => OnChange?.Invoke();
}