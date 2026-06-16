// file: shared_export_symbols.c
#include "shared_export_symbols_private.h"


__attribute__((visibility("default"))) int exportedValue = 3;
int publicValue = 4;
__attribute__((visibility("default"))) int exportedAdd(int value)
{
	return (value + exportedValue);
}

int publicAdd(int value)
{
	return (exportedAdd(value) + publicValue);
}

// file: shared_export_symbols.h
#ifndef SHARED_EXPORT_SYMBOLS_H_
#define SHARED_EXPORT_SYMBOLS_H_

#include "shared_export_symbols_private.h"

__attribute__((visibility("default"))) int exportedAdd(int value);
extern __attribute__((visibility("default"))) int exportedValue;

#endif
// file: shared_export_symbols_private.h
#ifndef SHARED_EXPORT_SYMBOLS_PRIVATE_H_
#define SHARED_EXPORT_SYMBOLS_PRIVATE_H_

#include <stddef.h>
#include <stdint.h>
#include <stdbool.h>

/* Forward declarations. */

/* Enums. */

/* Newtypes. */

/* Layouts. */

/* Function declarations. */
__attribute__((visibility("default"))) int exportedAdd(int value);
int publicAdd(int value);

/* Object declarations. */
extern __attribute__((visibility("default"))) int exportedValue;
extern int publicValue;


#endif
