// file: alias_canonical_emit.c
#include "alias_canonical_emit_private.h"
#include "alias_canonical_emit.h"

/* Private file declarations. */
static int __attribute__((sysv_abi)) increment(int value);

static int __attribute__((sysv_abi)) increment(int value)
{
	return (value + 1);
}

int main(void)
{
	int value = 1;
	return increment(value);
}

// file: alias_canonical_emit.h
#ifndef ALIAS_CANONICAL_EMIT_H_
#define ALIAS_CANONICAL_EMIT_H_

#include "alias_canonical_emit_private.h"

int main(void);

#endif
// file: alias_canonical_emit_private.h
#ifndef ALIAS_CANONICAL_EMIT_PRIVATE_H_
#define ALIAS_CANONICAL_EMIT_PRIVATE_H_

#include <stddef.h>
#include <stdint.h>
#include <stdbool.h>

/* Forward declarations. */

/* Newtypes. */

/* Enums. */

/* Layouts. */

/* Function declarations. */
int main(void);

/* Object declarations. */


#endif
