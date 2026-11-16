using System;
using System.Runtime.InteropServices;

namespace Budget_Summary
{
    public interface ITaxFeeAdapter
    {
        Result<int, DomainError> ComputeTaxCents(decimal amount, int taxBps);
    }

    public sealed class NativeTaxFeeAdapter : ITaxFeeAdapter
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
                    new DomainError("AMOUNT_TOO_LARGE", "Amount is too large for the native call."));

            try
            {
                int tax = NativeMethods.expense_tax_cents((int)centsDecimal, taxBps);
                if (tax < 0)
                    return Result<int, DomainError>.Failure(
                        new DomainError("NATIVE_INVALID_INPUT", "Native function rejected the input."));
                return Result<int, DomainError>.Success(tax);
            }
            catch (DllNotFoundException ex)
            { return Result<int, DomainError>.Failure(new DomainError("NATIVE_LIBRARY_NOT_FOUND", ex.Message)); }
            catch (EntryPointNotFoundException ex)
            { return Result<int, DomainError>.Failure(new DomainError("NATIVE_ENTRYPOINT_NOT_FOUND", ex.Message)); }
            catch (BadImageFormatException ex)
            { return Result<int, DomainError>.Failure(new DomainError("NATIVE_BAD_IMAGE", ex.Message)); }
            catch (Exception ex)
            { return Result<int, DomainError>.Failure(new DomainError("NATIVE_CALL_FAILED", ex.Message)); }
        }
    }

    public sealed class FailingTaxFeeAdapter : ITaxFeeAdapter
    {
        private readonly string _code, _message;
        public FailingTaxFeeAdapter(string code = "NATIVE_UNAVAILABLE",
                                    string message = "Simulated native adapter failure.")
        { _code = code; _message = message; }

        public Result<int, DomainError> ComputeTaxCents(decimal amount, int taxBps) =>
            Result<int, DomainError>.Failure(new DomainError(_code, _message));
    }

    internal static class NativeMethods
    {
        [DllImport("budget_native", CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "expense_tax_cents")]
        internal static extern int expense_tax_cents(int amountCents, int taxBps);
    }
}