// file: testonly_production_omits.c
#include "testonly_production_omits_private.h"

/* Private file declarations. */
static int productionValue(void);

static int productionValue(void)
{
	return 1;
}

// file: testonly_production_omits_private.h
#ifndef TESTONLY_PRODUCTION_OMITS_PRIVATE_H_
#define TESTONLY_PRODUCTION_OMITS_PRIVATE_H_

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
