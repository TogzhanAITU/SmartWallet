#ifndef BUDGET_NATIVE_H
#define BUDGET_NATIVE_H
#include <stdint.h>
#ifdef __cplusplus
extern "C" {
#endif
int32_t expense_tax_cents(int32_t amount_cents, int32_t tax_bps);
#ifdef __cplusplus
}
#endif
#endif