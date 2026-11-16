using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Budget_Summary
{
    public record ExpenseDraft(decimal Amount, bool IsMandatory);
    public record ValidatedExpense(decimal Amount, bool IsMandatory);
    public record TaxedExpense(decimal Amount, decimal TaxAmount);

    public class ExpenseReceipt
    {
        public string TransactionId { get; }
        public decimal Amount { get; }
        public decimal TaxAmount { get; }
        public bool IsFinalized { get; internal set; }

        public ExpenseReceipt(string transactionId, decimal amount, decimal taxAmount, bool isFinalized = true)
        {
            TransactionId = transactionId;
            Amount = amount;
            TaxAmount = taxAmount;
            IsFinalized = isFinalized;
        }
    }

    public record DomainError(string Code, string Message);

    public class Result<T, E>
    {
        private readonly T _value;
        private readonly E _error;

        public bool IsSuccess { get; }

        private Result(T value)  { _value = value; _error = default!; IsSuccess = true; }
        private Result(E error)  { _value = default!; _error = error; IsSuccess = false; }

        public static Result<T, E> Success(T value) => new(value);
        public static Result<T, E> Failure(E error) => new(error);

        public TValue Match<TValue>(Func<T, TValue> onSuccess, Func<E, TValue> onFailure) =>
            IsSuccess ? onSuccess(_value) : onFailure(_error);

        public void Handle(Action<T> onSuccess, Action<E> onFailure)
        {
            if (IsSuccess) onSuccess(_value); else onFailure(_error);
        }

        public T Value => IsSuccess
            ? _value
            : throw new InvalidOperationException("Cannot read Value from a failed Result.");

        public E Error => !IsSuccess
            ? _error
            : throw new InvalidOperationException("Cannot read Error from a successful Result.");
    }

    public abstract class Maybe<T>
    {
        public sealed class Some(T value) : Maybe<T> { public T Value { get; } = value; }
        public sealed class None : Maybe<T> { }
    }

    public record ExpenseRecord(string Id, decimal Amount)
    {
        public string Category { get; init; } = "Uncategorized";
        public bool IsMandatory { get; init; } = false;
        public int Quantity { get; init; } = 1;
        public DateTime CreatedAt { get; init; } = DateTime.UnixEpoch;
    }

    public static class BudgetWorkflow
    {
        public static ExpenseDraft Start(decimal amount, bool isMandatory = false) =>
            new(amount, isMandatory);

        public static Result<ValidatedExpense, DomainError> Validate(ExpenseDraft draft)
        {
            if (draft.Amount <= 0)
                return Result<ValidatedExpense, DomainError>.Failure(
                    new DomainError("INVALID_AMOUNT",
                        $"Amount {draft.Amount} must be greater than zero."));
            return Result<ValidatedExpense, DomainError>.Success(
                new ValidatedExpense(draft.Amount, draft.IsMandatory));
        }

        public static Result<TaxedExpense, DomainError> ApplyTaxPolicy(
            ValidatedExpense validated, decimal taxRate, decimal nonMandatoryMultiplier)
        {
            if (taxRate < 0 || taxRate > 1)
                return Result<TaxedExpense, DomainError>.Failure(
                    new DomainError("INVALID_TAX_RATE", "Tax rate must be between 0 and 1."));

            if (nonMandatoryMultiplier < 0)
                return Result<TaxedExpense, DomainError>.Failure(
                    new DomainError("INVALID_MULTIPLIER", "Non-mandatory multiplier must be non-negative."));

            decimal effectiveRate = validated.IsMandatory ? taxRate : taxRate * nonMandatoryMultiplier;
            decimal taxAmount = Math.Round(validated.Amount * effectiveRate, 2);
            return Result<TaxedExpense, DomainError>.Success(
                new TaxedExpense(validated.Amount + taxAmount, taxAmount));
        }

        public static Result<U, E> Map<T, U, E>(Result<T, E> result, Func<T, U> mapper)
        {
            if (!result.IsSuccess) return Result<U, E>.Failure(result.Match(_ => default!, e => e));
            return Result<U, E>.Success(mapper(result.Match(v => v, _ => default!)));
        }

        public static Result<U, E> Bind<T, U, E>(Result<T, E> result, Func<T, Result<U, E>> binder)
        {
            if (!result.IsSuccess) return Result<U, E>.Failure(result.Match(_ => default!, e => e));
            return binder(result.Match(v => v, _ => default!));
        }

        public static Result<ExpenseReceipt, DomainError> Finalize(
            TaxedExpense taxed, Func<decimal, string> gateway)
        {
            try
            {
                string txId = gateway(taxed.Amount);
                return Result<ExpenseReceipt, DomainError>.Success(
                    new ExpenseReceipt(txId, taxed.Amount, taxed.TaxAmount));
            }
            catch (Exception ex)
            {
                return Result<ExpenseReceipt, DomainError>.Failure(
                    new DomainError("GATEWAY_ERROR", ex.Message));
            }
        }

        private static readonly HashSet<TaxedExpense> _finalized = new();

        public static Result<ExpenseReceipt, DomainError> FinalizeOnce(
            TaxedExpense taxed, Func<decimal, string> gateway)
        {
            lock (_finalized)
            {
                if (!_finalized.Add(taxed))
                    return Result<ExpenseReceipt, DomainError>.Failure(
                        new DomainError("ALREADY_FINALIZED",
                            "This taxed expense has already been finalized."));
            }
            return Finalize(taxed, gateway);
        }
    }

    public static class ExpenseDataRepository
    {
        public static Result<List<ExpenseRecord>, DomainError> LoadFromJson(string filePath)
        {
            try
            {
                if (!File.Exists(filePath))
                    return Result<List<ExpenseRecord>, DomainError>.Failure(
                        new DomainError("FILE_NOT_FOUND",
                            $"The data file at '{filePath}' was not found."));

                using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var reader = new StreamReader(stream);
                string json = reader.ReadToEnd();

                var expenses = JsonSerializer.Deserialize<List<ExpenseRecord>>(json);
                if (expenses is null)
                    return Result<List<ExpenseRecord>, DomainError>.Failure(
                        new DomainError("MALFORMED_FILE",
                            "The JSON content could not be parsed into expenses."));

                return Result<List<ExpenseRecord>, DomainError>.Success(expenses);
            }
            catch (JsonException ex)
            {
                return Result<List<ExpenseRecord>, DomainError>.Failure(
                    new DomainError("MALFORMED_JSON", $"JSON parsing error: {ex.Message}"));
            }
            catch (Exception ex)
            {
                return Result<List<ExpenseRecord>, DomainError>.Failure(
                    new DomainError("BOUNDARY_EXCEPTION", $"Operational error loading file: {ex.Message}"));
            }
        }

        private static int _iterationCounter;
        public static int IterationCounter => _iterationCounter;

        public static IEnumerable<string> StreamExpenseLines(string filePath)
        {
            _iterationCounter = 0;
            if (!File.Exists(filePath)) yield break;

            using var reader = new StreamReader(filePath);
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                _iterationCounter++;
                yield return line;
            }
        }
    }

    public static class TaxPolicyLookup
    {
        private static readonly Dictionary<string, (decimal Rate, decimal Multiplier)> _policies = new()
        {
            { "Standard", (0.10m, 1.5m) },
            { "Discount", (0.05m, 0.5m) },
            { "Increase", (0.15m, 2.0m) }
        };

        public static Maybe<(decimal Rate, decimal Multiplier)> Find(string name) =>
            _policies.TryGetValue(name, out var p)
                ? new Maybe<(decimal, decimal)>.Some(p)
                : new Maybe<(decimal, decimal)>.None();
    }
}