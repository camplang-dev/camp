// file: nested_optional_components.c
#include "nested_optional_components_private.h"

/* Private file declarations. */
static void main(void);

int one(int a, bool a_specified, bool *result_specified)
{
	{
		(*result_specified) = a_specified;
		return a;
	}
}

int *array(int *a, uintptr_t a_length, bool a_specified, uintptr_t *result_length, bool *result_specified)
{
	{
		(*result_length) = a_length;
		(*result_specified) = a_specified;
		return a;
	}
}

Predicate callable(Predicate d, void *d_context, bool d_specified, void **result_context, bool *result_specified)
{
	{
		(*result_context) = d_context;
		(*result_specified) = d_specified;
		return d;
	}
}

static void main(void)
{
}

// file: nested_optional_components.h
#ifndef NESTED_OPTIONAL_COMPONENTS_H_
#define NESTED_OPTIONAL_COMPONENTS_H_

#include "nested_optional_components_private.h"

int one(int a, bool a_specified, bool *result_specified);
int *array(int *a, uintptr_t a_length, bool a_specified, uintptr_t *result_length, bool *result_specified);
Predicate callable(Predicate d, void *d_context, bool d_specified, void **result_context, bool *result_specified);

#endif
// file: nested_optional_components_private.h
#ifndef NESTED_OPTIONAL_COMPONENTS_PRIVATE_H_
#define NESTED_OPTIONAL_COMPONENTS_PRIVATE_H_

#include <stddef.h>
#include <stdint.h>
#include <stdbool.h>

/* Forward declarations. */
typedef struct State State;

/* Enums. */

/* Newtypes. */
typedef int (* Predicate)(void *context, int n);

/* Layouts. */
struct State
{
	int a;
	bool a_specified;
	int *array;
	uintptr_t array_length;
	bool array_specified;
	int (* callback)(void *arg0, int arg1);
	void *callback_context;
	bool callback_specified;
};

/* Function declarations. */
int one(int a, bool a_specified, bool *result_specified);
int *array(int *a, uintptr_t a_length, bool a_specified, uintptr_t *result_length, bool *result_specified);
Predicate callable(Predicate d, void *d_context, bool d_specified, void **result_context, bool *result_specified);

/* Object declarations. */


#endif
