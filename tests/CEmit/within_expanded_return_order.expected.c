// file: within_expanded_return_order.c
#include "within_expanded_return_order_private.h"

/* Private file declarations. */
void *malloc(uintptr_t size);
static void *Allocator_alloc(Allocator *this, uintptr_t size);
static char *duplicate(const char *values, uintptr_t values_length, Allocator *allocator, uintptr_t *result_length);
static char *forwardDuplicate(const char *values, uintptr_t values_length, Allocator *allocator, uintptr_t *result_length);

static void *Allocator_alloc(Allocator *this, uintptr_t size)
{
	return malloc(size);
}

static char *duplicate(const char *values, uintptr_t values_length, Allocator *allocator, uintptr_t *result_length)
{
	char *copy = (char *)(((allocator != NULL) ? Allocator_alloc(allocator, (sizeof(char) * values_length)) : malloc((sizeof(char) * values_length))));
	uintptr_t copy_length = values_length;
	{
		(*result_length) = copy_length;
		return copy;
	}
}

static char *forwardDuplicate(const char *values, uintptr_t values_length, Allocator *allocator, uintptr_t *result_length)
{
	return duplicate(values, values_length, allocator, result_length);
}

int main(void)
{
	return 0;
}

// file: within_expanded_return_order.h
#ifndef WITHIN_EXPANDED_RETURN_ORDER_H_
#define WITHIN_EXPANDED_RETURN_ORDER_H_

#include "within_expanded_return_order_private.h"

int main(void);

#endif
// file: within_expanded_return_order_private.h
#ifndef WITHIN_EXPANDED_RETURN_ORDER_PRIVATE_H_
#define WITHIN_EXPANDED_RETURN_ORDER_PRIVATE_H_

#include <stddef.h>
#include <stdint.h>
#include <stdbool.h>

/* Forward declarations. */
typedef struct Allocator Allocator;

/* Enums. */

/* Newtypes. */

/* Layouts. */
struct Allocator
{
	char _camp_empty;
};

/* Function declarations. */
int main(void);

/* Object declarations. */


#endif
