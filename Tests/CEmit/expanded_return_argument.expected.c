// file: expanded_return_argument.c
#include "expanded_return_argument_private.h"

/* Private file declarations. */
static int main(void);
static const char *getName(uintptr_t *result_length);
static void takesName(const char *name, uintptr_t name_length);

static int main(void)
{
	const char *_elements0;
	uintptr_t _length1;
	_elements0 = getName(&_length1);
	takesName(_elements0, _length1);
	return 0;
}

static const char *getName(uintptr_t *result_length)
{
	{
		(*result_length) = 4;
		return "john";
	}
}

static void takesName(const char *name, uintptr_t name_length)
{
}

// file: expanded_return_argument_private.h
#ifndef EXPANDED_RETURN_ARGUMENT_PRIVATE_H_
#define EXPANDED_RETURN_ARGUMENT_PRIVATE_H_

#include <stddef.h>
#include <stdint.h>
#include <stdbool.h>

/* Forward declarations. */

/* Enums. */

/* Newtypes. */

/* Layouts. */

/* Function declarations. */

/* Object declarations. */


#endif
