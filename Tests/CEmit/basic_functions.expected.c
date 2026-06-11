// file: basic_functions.c
#include "basic_functions_private.h"

/* Private file declarations. */
static int privateSquare(int value);

int add(int left, int right)
{
	return (left + right);
}

static int privateSquare(int value)
{
	return (value * value);
}

// file: basic_functions.h
#ifndef BASIC_FUNCTIONS_H_
#define BASIC_FUNCTIONS_H_

#include "basic_functions_private.h"

int add(int left, int right);

#endif
// file: basic_functions_private.h
#ifndef BASIC_FUNCTIONS_PRIVATE_H_
#define BASIC_FUNCTIONS_PRIVATE_H_

#include <stddef.h>
#include <stdint.h>
#include <stdbool.h>

/* Forward declarations. */

/* Newtypes. */

/* Enums. */

/* Layouts. */

/* Function declarations. */
int add(int left, int right);

/* Object declarations. */


#endif
