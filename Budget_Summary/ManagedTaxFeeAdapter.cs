using System;

namespace Budget_Summary
{
    public sealed class ManagedTaxFeeAdapter : ITaxFeeAdapter
    {
        public Result<int, DomainError> ComputeTaxCents(decimal amount, int taxBps)
        {
            if (amount < 0)
                return Result<int, DomainError>.Failure(
                    new DomainError("INVALID_AMOUNT", "Amount must not be negative."));
            if (taxBps < 0 || taxBps > 5000)
                return Result<int, DomainError>.Failure(
                    new DomainError("INVALID_TAX_BPS", "Tax basis points must be between 0 and 5000."));

            decimal centsDecimal = amount * 100m;
            if (centsDecimal != decimal.Truncate(centsDecimal))
                return Result<int, DomainError>.Failure(
                    new DomainError("INVALID_AMOUNT_PRECISION",
                        "Amount must be representable in whole cents."));
            if (centsDecimal > int.MaxValue)
                return Result<int, DomainError>.Failure(
                    new DomainError("AMOUNT_TOO_LARGE", "Amount is too large."));

            long amountCents = (long)centsDecimal;
            long product = amountCents * taxBps;
            long tax = (product + 5000) / 10000;

            if (tax > int.MaxValue)
                return Result<int, DomainError>.Failure(
                    new DomainError("TAX_TOO_LARGE", "Computed tax exceeds int range."));

            return Result<int, DomainError>.Success((int)tax);
        }
    }
}