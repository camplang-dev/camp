// file: testonly_test_module_includes.c
#include "testonly_test_module_includes_private.h"

/* Private file declarations. */
static int productionValue(void);
static int hiddenHelper(void);

static int productionValue(void)
{
	return 1;
}

static int hiddenHelper(void)
{
	return 2;
}

// file: testonly_test_module_includes_private.h
#ifndef TESTONLY_TEST_MODULE_INCLUDES_PRIVATE_H_
#define TESTONLY_TEST_MODULE_INCLUDES_PRIVATE_H_

#include <stddef.h>
#include <stdint.h>
#include <stdbool.h>

/* Forward declarations. */
typedef struct Hidden Hidden;

/* Enums. */

/* Newtypes. */

/* Layouts. */
struct Hidden
{
	int value;
};

/* Function declarations. */

/* Object declarations. */


#endif
