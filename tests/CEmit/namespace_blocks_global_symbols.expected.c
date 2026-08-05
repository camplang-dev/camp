// file: namespace_blocks_global_symbols.c
#include "namespace_blocks_global_symbols_private.h"


int App_appValue(void)
{
	return 1;
}

int Tools_toolValue(void)
{
	return 2;
}

int rootValue(void)
{
	return 3;
}

// file: namespace_blocks_global_symbols.h
#ifndef NAMESPACE_BLOCKS_GLOBAL_SYMBOLS_H_
#define NAMESPACE_BLOCKS_GLOBAL_SYMBOLS_H_

#include "namespace_blocks_global_symbols_private.h"

int App_appValue(void);
int Tools_toolValue(void);
int rootValue(void);

#endif
// file: namespace_blocks_global_symbols_private.h
#ifndef NAMESPACE_BLOCKS_GLOBAL_SYMBOLS_PRIVATE_H_
#define NAMESPACE_BLOCKS_GLOBAL_SYMBOLS_PRIVATE_H_

#include <stddef.h>
#include <stdint.h>
#include <stdbool.h>

/* Forward declarations. */

/* Enums. */

/* Newtypes. */

/* Layouts. */

/* Function declarations. */
int App_appValue(void);
int Tools_toolValue(void);
int rootValue(void);

/* Object declarations. */


#endif
