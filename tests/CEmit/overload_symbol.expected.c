// file: overload_symbol.c
#include "overload_symbol_private.h"

/* Private file declarations. */
double sqrt(double value);
float sqrtf(float value);
static void main(void);

static void main(void)
{
	double a = sqrt((double)(9));
	float b = sqrtf((float)(9));
}

// file: overload_symbol_private.h
#ifndef OVERLOAD_SYMBOL_PRIVATE_H_
#define OVERLOAD_SYMBOL_PRIVATE_H_

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
