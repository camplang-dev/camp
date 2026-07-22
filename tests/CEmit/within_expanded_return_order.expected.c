// file: within_expanded_return_order.c
#include "within_expanded_return_order_private.h"

/* Private file declarations. */
void *malloc(uintptr_t size);
static void *Allocator_alloc(Allocator *this, uintptr_t size);
static void *duplicate(const void *values, uintptr_t values_length, uintptr_t sizeof_T, Allocator *allocator, uintptr_t *result_length);
static void *forwardDuplicate(const void *values, uintptr_t values_length, uintptr_t sizeof_T, Allocator *allocator, uintptr_t *result_length);

static void *Allocator_alloc(Allocator *this, uintptr_t size)
{
	return malloc(size);
}

static void *duplicate(const void *values, uintptr_t values_length, uintptr_t sizeof_T, Allocator *allocator, uintptr_t *result_length)
{
	void *copy = (void*)(((allocator != NULL) ? Allocator_alloc(allocator, (sizeof_T * values_length)) : malloc((sizeof_T * values_length))));
	uintptr_t copy_length = values_length;
	{
		(*result_length) = copy_length;
		return copy;
	}
}

static void *forwardDuplicate(const void *values, uintptr_t values_length, uintptr_t sizeof_T, Allocator *allocator, uintptr_t *result_length)
{
	return duplicate(values, values_length, sizeof_T, allocator, result_length);
}

int main(void)
{
	Allocator allocator = (Allocator){0};
	int *values = (int []){1, 2, 3};
	uintptr_t values_length = 3;
	int *copied;
	uintptr_t copied_length;
	copied = forwardDuplicate(values, values_length, sizeof(int), &allocator, &copied_length);
	return ((copied_length == 3) ? 0 : 1);
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
