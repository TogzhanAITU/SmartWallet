#include "budget_native.h"
#include <stdint.h>

int32_t expense_tax_cents(int32_t amount_cents, int32_t tax_bps)
{
    if (amount_cents < 0 || tax_bps < 0 || tax_bps > 5000)
        return -1;

    int64_t product = (int64_t)amount_cents * (int64_t)tax_bps;
    int64_t tax = (product + 5000) / 10000;  /* round half-up */

    if (tax > 2147483647LL)
        return -1;

    return (int32_t)tax;
}