// file: namespace_source_lookup_symbols.c
#include "namespace_source_lookup_symbols_private.h"

/* Private file declarations. */
static int SourceBoundary_increment(int value);
static int (* SourceBoundary_getIncrement)(int camp)(void);

static int SourceBoundary_increment(int value)
{
	return (value + 1);
}

static int (* SourceBoundary_getIncrement)(int camp)(void)
{
	return SourceBoundary_increment;
}

int SourceBoundary_main(void)
{
	int (* f)(int camp) = SourceBoundary_increment;
	int (* g)(int camp) = SourceBoundary_getIncrement();
	return ((((SourceBoundary_increment(1) + SourceBoundary_increment(2)) + SourceBoundary_increment(3)) + f(4)) + g(5));
}

// file: namespace_source_lookup_symbols.h
#ifndef NAMESPACE_SOURCE_LOOKUP_SYMBOLS_H_
#define NAMESPACE_SOURCE_LOOKUP_SYMBOLS_H_

#include "namespace_source_lookup_symbols_private.h"

int SourceBoundary_main(void);

#endif
// file: namespace_source_lookup_symbols_private.h
#ifndef NAMESPACE_SOURCE_LOOKUP_SYMBOLS_PRIVATE_H_
#define NAMESPACE_SOURCE_LOOKUP_SYMBOLS_PRIVATE_H_

#include <stddef.h>
#include <stdint.h>
#include <stdbool.h>

/* Forward declarations. */

/* Enums. */

/* Newtypes. */

/* Layouts. */

/* Function declarations. */
int SourceBoundary_main(void);

/* Object declarations. */


#endif
