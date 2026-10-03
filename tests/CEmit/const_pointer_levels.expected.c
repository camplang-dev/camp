// file: const_pointer_levels.c
#include "const_pointer_levels_private.h"


int readConstPointer(int * const *value)
{
	return **value;
}

int readConstToConst(const int * const *value)
{
	return **value;
}

int readVolatilePointer(int * volatile *value)
{
	return **value;
}

int readDeepPointer(int * const * const *value)
{
	return ***value;
}

// file: const_pointer_levels.h
#ifndef CONST_POINTER_LEVELS_H_
#define CONST_POINTER_LEVELS_H_

#include "const_pointer_levels_private.h"

int readConstPointer(int * const *value);
int readConstToConst(const int * const *value);
int readVolatilePointer(int * volatile *value);
int readDeepPointer(int * const * const *value);

#endif
// file: const_pointer_levels_private.h
#ifndef CONST_POINTER_LEVELS_PRIVATE_H_
#define CONST_POINTER_LEVELS_PRIVATE_H_

#include <stddef.h>
#include <stdint.h>
#include <stdbool.h>

/* Forward declarations. */

/* Enums. */

/* Newtypes. */

/* Layouts. */

/* Function declarations. */
int readConstPointer(int * const *value);
int readConstToConst(const int * const *value);
int readVolatilePointer(int * volatile *value);
int readDeepPointer(int * const * const *value);

/* Object declarations. */


#endif
